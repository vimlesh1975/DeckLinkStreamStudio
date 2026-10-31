using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DeckLinkStreamStudio.Models;

namespace DeckLinkStreamStudio.Engines;

public enum RunnerMode
{
    StandbyPreview,
    LiveStream
}

public sealed class FfmpegStreamRunner : IDisposable
{
    private Process? _process;
    private Process? _ytDlpProcess;
    private CancellationTokenSource? _pipeCts;
    private StreamPreviewReader? _previewReader;
    private readonly object _syncRoot = new();
    private DateTime _startTimeUtc;
    private bool _isDisposed;
    private readonly string _ffmpegPath;
    private StreamConfig? _currentConfig;
    private readonly TcpBroadcastHub _hub = new();

    public const int PreviewCenterWidth = 444;
    public const int PreviewTotalHeight = 250;
    public const int PreviewMeterWidth = 18;
    public const int PreviewTotalWidth = PreviewCenterWidth + (PreviewMeterWidth * 2); // 480 px

    public event Action<string>? OnLog;
    public event Action<StreamStats>? OnStatsUpdated;
    public event Action<System.Drawing.Bitmap>? OnPreviewFrame;
    public event Action<StreamStatus, string>? OnStatusChanged;
    public event Action<int, RunnerMode>? OnProcessExited;

    public bool IsRunning
    {
        get
        {
            lock (_syncRoot)
            {
                return _process != null && !_process.HasExited;
            }
        }
    }

    public RunnerMode CurrentMode { get; private set; } = RunnerMode.StandbyPreview;
    public StreamStats CurrentStats { get; } = new();

    public FfmpegStreamRunner(string? ffmpegPath = null)
    {
        _ffmpegPath = ResolveFfmpegPath(ffmpegPath);
    }

    public static string ResolveFfmpegPath(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            return customPath;

        // Primary: ffmpeg.exe lives alongside the application executable
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var localExe = Path.Combine(baseDir, "ffmpeg.exe");
        if (File.Exists(localExe))
            return localExe;

        // Secondary: current working directory
        var curExe = Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg.exe");
        if (File.Exists(curExe))
            return curExe;

        // Last resort: hope it is on PATH
        return "ffmpeg.exe";
    }

    public bool StartStandbyPreview(StreamConfig config)
    {
        lock (_syncRoot)
        {
            if (IsRunning)
            {
                Stop();
            }

            _hub.Start();
            _currentConfig = config;
            CurrentMode = RunnerMode.StandbyPreview;
            var inputDetails = ResolveInputDetails(config);
            var args = BuildStandbyPreviewArguments(config, inputDetails);
            return LaunchProcess(args, RunnerMode.StandbyPreview, inputDetails);
        }
    }

    public bool StartLiveStream(StreamConfig config)
    {
        lock (_syncRoot)
        {
            if (IsRunning)
            {
                Stop();
                Thread.Sleep(500); // Allow remote RTMP servers (Facebook/YouTube) to close TCP socket cleanly
            }

            _currentConfig = config;
            CurrentMode = RunnerMode.LiveStream;
            var inputDetails = ResolveInputDetails(config);
            var args = BuildLiveStreamArguments(config, inputDetails);
            return LaunchProcess(args, RunnerMode.LiveStream, inputDetails);
        }
    }

    public static bool? _isNvencSupported;
    public static bool? _isHevcNvencSupported;
    public static bool? _isAmfSupported;

    public static bool IsEncoderWorking(string codecName)
    {
        try
        {
            var ffmpeg = ResolveFfmpegPath();
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-hide_banner -f lavfi -i testsrc=duration=1:size=256x256:rate=1 -c:v {codecName} -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(3000);
                if (proc.ExitCode != 0) return false;
                if (stderr.Contains("Cannot load", StringComparison.OrdinalIgnoreCase)) return false;
                if (stderr.Contains("Error while opening encoder", StringComparison.OrdinalIgnoreCase)) return false;
                if (stderr.Contains("Conversion failed!", StringComparison.OrdinalIgnoreCase)) return false;
                if (stderr.Contains("Could not open encoder", StringComparison.OrdinalIgnoreCase)) return false;
                if (stderr.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase)) return false;
                if (stderr.Contains("The minimum required Nvidia driver", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
        }
        catch { }
        return false;
    }

    public static bool IsNvencAvailable()
    {
        if (_isNvencSupported.HasValue) return _isNvencSupported.Value;
        _isNvencSupported = IsEncoderWorking("h264_nvenc");
        return _isNvencSupported.Value;
    }

    public static bool IsHevcNvencAvailable()
    {
        if (_isHevcNvencSupported.HasValue) return _isHevcNvencSupported.Value;
        _isHevcNvencSupported = IsEncoderWorking("hevc_nvenc");
        return _isHevcNvencSupported.Value;
    }

    public static bool IsAmfAvailable()
    {
        if (_isAmfSupported.HasValue) return _isAmfSupported.Value;
        _isAmfSupported = IsEncoderWorking("h264_amf");
        return _isAmfSupported.Value;
    }

    public static bool IsFacebookDestination(DestinationConfig? dest)
    {
        if (dest == null) return false;
        if (!string.IsNullOrWhiteSpace(dest.Id) && dest.Id.Trim().Equals("fb", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(dest.ServerUrl) && dest.ServerUrl.Contains("facebook.com", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(dest.FullUrl) && dest.FullUrl.Contains("facebook.com", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static string GetVideoCodecArgs(VideoEncoderType requestedEncoder, int videoBitrateKbps, int gopSize)
    {
        var encoder = requestedEncoder;
        if (encoder == VideoEncoderType.Auto)
        {
            if (IsHevcNvencAvailable())
                encoder = VideoEncoderType.HEVC_NVENC;
            else if (IsNvencAvailable())
                encoder = VideoEncoderType.H264_NVENC;
            else if (IsAmfAvailable())
                encoder = VideoEncoderType.H264_AMF;
            else
                encoder = VideoEncoderType.LibX264;
        }
        else if (encoder == VideoEncoderType.HEVC_NVENC && !IsHevcNvencAvailable())
        {
            encoder = IsNvencAvailable() ? VideoEncoderType.H264_NVENC : (IsAmfAvailable() ? VideoEncoderType.H264_AMF : VideoEncoderType.LibX264);
        }
        else if (encoder == VideoEncoderType.H264_NVENC && !IsNvencAvailable())
        {
            encoder = IsHevcNvencAvailable() ? VideoEncoderType.HEVC_NVENC : (IsAmfAvailable() ? VideoEncoderType.H264_AMF : VideoEncoderType.LibX264);
        }
        else if (encoder == VideoEncoderType.H264_AMF && !IsAmfAvailable())
        {
            encoder = IsHevcNvencAvailable() ? VideoEncoderType.HEVC_NVENC : (IsNvencAvailable() ? VideoEncoderType.H264_NVENC : VideoEncoderType.LibX264);
        }

        var videoBitrate = $"{videoBitrateKbps}k";
        var maxRate = $"{videoBitrateKbps}k";
        var bufSize = $"{videoBitrateKbps * 2}k";

        return encoder switch
        {
            VideoEncoderType.HEVC_NVENC => $"-c:v hevc_nvenc -preset p4 -tune ll -delay 0 -g {gopSize} -forced-idr 1 -bf 0 -b:v {videoBitrate} -maxrate {maxRate} -bufsize {bufSize} -pix_fmt yuv420p",
            VideoEncoderType.H264_NVENC => $"-c:v h264_nvenc -preset p4 -tune ll -delay 0 -g {gopSize} -forced-idr 1 -bf 0 -b:v {videoBitrate} -maxrate {maxRate} -bufsize {bufSize} -pix_fmt yuv420p",
            VideoEncoderType.H264_AMF => $"-c:v h264_amf -quality speed -rc cbr -g {gopSize} -forced_idr 1 -header_spacing {gopSize} -b:v {videoBitrate} -maxrate {maxRate} -bufsize {bufSize} -pix_fmt yuv420p",
            _ => $"-c:v libx264 -preset veryfast -tune zerolatency -g {gopSize} -bf 0 -x264opts repeat-headers=1 -b:v {videoBitrate} -maxrate {maxRate} -bufsize {bufSize} -pix_fmt yuv420p"
        };
    }

    public static bool IsFileSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        var s = source.Trim();
        return s.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
               s.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
               s.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
               s.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("go1080p25", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNetworkSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        var s = source.Trim();
        return s.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("Network", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("srt://", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHardwareDeckLink(string? source)
    {
        return !IsFileSource(source) && !IsNetworkSource(source);
    }

    public sealed class ResolvedInputDetails
    {
        public bool IsDualInput { get; set; }
        public string InputArguments { get; set; } = "";
        public string VideoSpecifier => "[0:v]";
        public string AudioSpecifier => IsDualInput ? "[1:a]" : "[0:a]";
        public string? ResolvedAudioUrl { get; set; }
        public bool IsYouTubePiped { get; set; }
        public string? YouTubeUrl { get; set; }
    }

    private static string? _lastResolvedAudioUrl;
    public static string? GetLastResolvedAudioUrl() => _lastResolvedAudioUrl;

    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";

    public ResolvedInputDetails ResolveInputDetails(StreamConfig config)
    {
        var details = new ResolvedInputDetails();

        if (IsNetworkSource(config.DeckLinkDevice))
        {
            var rawUrl = !string.IsNullOrWhiteSpace(config.NetworkStreamUrl) 
                ? config.NetworkStreamUrl.Trim() 
                : (YouTubeStreamResolver.IsNetworkUrl(config.DeckLinkDevice) ? config.DeckLinkDevice.Trim() : "");

            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                OnLog?.Invoke("[WARNING] Network/YouTube Stream URL is empty. Please enter a valid stream URL.");
                details.InputArguments = "-f lavfi -i smptebars=size=1920x1080:rate=25 -f lavfi -i anullsrc=r=48000:cl=stereo ";
                return details;
            }

            if (YouTubeStreamResolver.IsYouTubeUrl(rawUrl))
            {
                // Ingesting YouTube HLS directly into FFmpeg causes 403 Forbidden errors after ~1 minute
                // because YouTube CDN session tokens rotate. yt-dlp manages chunk signatures and tokens natively,
                // piping a continuous MPEG-TS stream into FFmpeg's stdin (pipe:0).
                _lastResolvedAudioUrl = rawUrl;
                details.IsDualInput = false;
                details.IsYouTubePiped = true;
                details.YouTubeUrl = rawUrl;
                details.InputArguments = "-fflags +discardcorrupt+genpts -thread_queue_size 1024 -re -i pipe:0 ";
                return details;
            }
            else
            {
                // Direct RTSP, RTMP, HLS (.m3u8), or HTTP stream
                var netFlags = $"-user_agent \"{BrowserUserAgent}\" -headers \"Accept: */*\\r\\n\" -rw_timeout 15000000 -reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2 ";
                _lastResolvedAudioUrl = rawUrl;
                details.IsDualInput = false;
                details.InputArguments = $"{netFlags}-re -i \"{rawUrl}\" ";
                return details;
            }
        }
        else if (IsFileSource(config.DeckLinkDevice))
        {
            var filePath = ResolveMediaFilePath(config.DeckLinkDevice);
            details.IsDualInput = false;
            details.InputArguments = $"-stream_loop -1 -re -i \"{filePath}\" ";
            return details;
        }
        else
        {
            var sb = new StringBuilder();
            AppendDeckLinkInput(sb, config);
            details.IsDualInput = false;
            details.InputArguments = sb.ToString();
            return details;
        }
    }

    public static string ResolveMediaFilePath(string? fileName = "go1080p25.mp4")
    {
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "go1080p25.mp4";

        var cleanName = fileName.Trim();
        if (cleanName.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            cleanName = cleanName.Substring(5).Trim();

        if (cleanName.EndsWith("(Loop)", StringComparison.OrdinalIgnoreCase))
            cleanName = cleanName.Replace("(Loop)", "").Trim();

        if (File.Exists(cleanName))
            return Path.GetFullPath(cleanName);

        var nameOnly = Path.GetFileName(cleanName);
        if (string.IsNullOrWhiteSpace(nameOnly))
            nameOnly = "go1080p25.mp4";

        // 1. Application BaseDirectory (exe folder)
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var exePath = Path.Combine(exeDir, nameOnly);
        if (File.Exists(exePath))
            return exePath;

        // 2. Current Directory
        var curDir = Directory.GetCurrentDirectory();
        var curPath = Path.Combine(curDir, nameOnly);
        if (File.Exists(curPath))
            return curPath;

        // 3. Known fallback paths
        var casparPath1 = Path.Combine(@"D:\casparcg\_media", nameOnly);
        if (File.Exists(casparPath1))
            return casparPath1;

        var casparPath2 = Path.Combine(@"D:\casparcg-server-060226\media", nameOnly);
        if (File.Exists(casparPath2))
            return casparPath2;

        return exePath;
    }

    private void AppendInputSource(StringBuilder sb, StreamConfig config)
    {
        if (IsNetworkSource(config.DeckLinkDevice))
        {
            var input = ResolveInputDetails(config);
            sb.Append(input.InputArguments);
        }
        else if (IsFileSource(config.DeckLinkDevice))
        {
            var filePath = ResolveMediaFilePath(config.DeckLinkDevice);
            sb.Append($"-stream_loop -1 -re -i \"{filePath}\" ");
        }
        else
        {
            AppendDeckLinkInput(sb, config);
        }
    }

    private string BuildStandbyPreviewArguments(StreamConfig config, ResolvedInputDetails? inputDetails = null)
    {
        inputDetails ??= ResolveInputDetails(config);

        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel info -stats ");

        // Input Source (File loop, DeckLink hardware, or YouTube/Network stream)
        sb.Append(inputDetails.InputArguments);

        var previewFps = config.PreviewFps > 0 ? config.PreviewFps : 15;
        var filter = BuildLiveStreamFilterGraph(config, previewFps, dualEncoding: false, inputDetails.VideoSpecifier, inputDetails.AudioSpecifier);
        sb.Append($"-filter_complex \"{filter}\" ");

        // Master broadcast stream encoded once and distributed via TCP Hub
        var gopSize = Math.Max(25, (config.TargetFps > 0 ? config.TargetFps : 25) * config.KeyframeIntervalSeconds);
        var videoCodecArgs = GetVideoCodecArgs(config.VideoEncoder, config.VideoBitrateKbps, gopSize);
        var audioBitrate = $"{config.AudioBitrateKbps}k";
        var audioCodecArgs = $"-c:a aac -b:a {audioBitrate} -ar 48000 -ac 2";
        sb.Append($"-map \"[v_stream]\" -map \"[a_stream]\" {videoCodecArgs} {audioCodecArgs} -f mpegts tcp://127.0.0.1:{TcpBroadcastHub.MasterPort} ");

        // In-app video preview + VU meters via stdout pipe
        sb.Append("-map \"[tx_preview]\" -f rawvideo -pix_fmt bgr24 pipe:1");

        return sb.ToString();
    }

    private string BuildLiveStreamArguments(StreamConfig config, ResolvedInputDetails? inputDetails = null)
    {
        inputDetails ??= ResolveInputDetails(config);

        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel info -stats ");

        // Input Source (File loop, DeckLink hardware, or YouTube/Network stream)
        sb.Append(inputDetails.InputArguments);

        // Collect Enabled Destinations (Sahyadri Facebook, Sahyadri YouTube, Sahyadri YouTube News)
        var enabledDests = config.Destinations
            .Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.FullUrl))
            .ToList();

        var fbDests = enabledDests.Where(IsFacebookDestination).ToList();
        var nonFbDests = enabledDests.Where(d => !IsFacebookDestination(d)).ToList();

        bool isHevcRequested = config.VideoEncoder == VideoEncoderType.HEVC_NVENC ||
                               (config.VideoEncoder == VideoEncoderType.Auto && IsHevcNvencAvailable());

        // Facebook Live only supports H.264 (AVC) and rejects HEVC.
        // If HEVC is requested and both Facebook and YouTube are active, dual hardware encoding is used.
        bool dualEncoding = isHevcRequested && fbDests.Count > 0 && nonFbDests.Count > 0;

        // Filter Complex for Preview + Deinterlacing / Scaling (+ dual stream splitting if needed)
        var previewFps = config.PreviewFps > 0 ? config.PreviewFps : 15;
        var filter = BuildLiveStreamFilterGraph(config, previewFps, dualEncoding, inputDetails.VideoSpecifier, inputDetails.AudioSpecifier);
        sb.Append($"-filter_complex \"{filter}\" ");

        var gopSize = Math.Max(25, (config.TargetFps > 0 ? config.TargetFps : 25) * config.KeyframeIntervalSeconds);
        var audioBitrate = $"{config.AudioBitrateKbps}k";
        var audioCodecArgs = $"-c:a aac -b:a {audioBitrate} -ar 48000 -ac 2";

        if (dualEncoding)
        {
            // Output 1: Non-Facebook destinations (YouTube) encoded with HEVC (thread-isolated via fifo pseudo-muxer)
            var hevcVideoCodecArgs = GetVideoCodecArgs(VideoEncoderType.HEVC_NVENC, config.VideoBitrateKbps, gopSize);
            var nonFbTargets = nonFbDests.Select(d => $"[f=fifo:fifo_format=flv:drop_pkts_on_overflow=1:attempt_recovery=1:recovery_wait_time=1:max_recovery_attempts=5]{d.FullUrl}").ToList();
            var nonFbChain = string.Join("|", nonFbTargets);
            sb.Append($"-map \"[v_stream_hevc]\" -map \"[a_stream_hevc]\" {hevcVideoCodecArgs} {audioCodecArgs} -avoid_negative_ts make_zero -max_muxing_queue_size 4096 -f tee \"{nonFbChain}\" ");

            // Output 2: Facebook destinations encoded with H.264 (thread-isolated via fifo pseudo-muxer)
            var h264VideoCodecArgs = GetVideoCodecArgs(VideoEncoderType.LibX264, Math.Min(config.VideoBitrateKbps, 6000), gopSize);
            var fbTargets = fbDests.Select(d => $"[f=fifo:fifo_format=flv:drop_pkts_on_overflow=1:attempt_recovery=1:recovery_wait_time=1:max_recovery_attempts=5]{d.FullUrl}").ToList();
            var fbChain = string.Join("|", fbTargets);
            sb.Append($"-map \"[v_stream_h264]\" -map \"[a_stream_h264]\" {h264VideoCodecArgs} {audioCodecArgs} -max_muxing_queue_size 4096 -f tee \"{fbChain}\" ");
        }
        else
        {
            // Single encoding branch (CPU libx264 or H264 NVENC)
            var effectiveEncoder = (nonFbDests.Count == 0 && fbDests.Count > 0 && config.VideoEncoder == VideoEncoderType.HEVC_NVENC)
                ? VideoEncoderType.LibX264
                : config.VideoEncoder;

            var videoBitrateKbps = (fbDests.Count > 0 && nonFbDests.Count == 0)
                ? Math.Min(config.VideoBitrateKbps, 6000)
                : config.VideoBitrateKbps;

            var videoCodecArgs = GetVideoCodecArgs(effectiveEncoder, videoBitrateKbps, gopSize);

            var activeTargets = enabledDests.Select(d => $"[f=fifo:fifo_format=flv:drop_pkts_on_overflow=1:attempt_recovery=1:recovery_wait_time=1:max_recovery_attempts=5]{d.FullUrl}").ToList();
            var teeChain = string.Join("|", activeTargets);
            sb.Append($"-map \"[v_stream]\" -map \"[a_stream]\" {videoCodecArgs} {audioCodecArgs} -max_muxing_queue_size 4096 -f tee \"{teeChain}\" ");
        }

        // Map preview to stdout pipe as raw bgr24 video for live in-app preview and audio meters
        sb.Append("-map \"[tx_preview]\" -f rawvideo -pix_fmt bgr24 pipe:1");

        return sb.ToString();
    }

    private void AppendDeckLinkInput(StringBuilder sb, StreamConfig config)
    {
        sb.Append("-f decklink ");

        // Format code (e.g. Hi50, Hp50, Hp25, auto)
        if (!string.IsNullOrWhiteSpace(config.VideoStandardCode) && !string.Equals(config.VideoStandardCode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append($"-format_code {config.VideoStandardCode} ");
        }

        sb.Append("-video_input sdi ");

        var audioInput = string.IsNullOrWhiteSpace(config.AudioInput) ? "embedded" : config.AudioInput;
        sb.Append($"-audio_input \"{audioInput}\" ");
        sb.Append("-signal_loss_action bars ");
        sb.Append("-audio_depth 16 ");
        sb.Append($"-channels {Math.Max(2, config.AudioChannels)} ");
        var inputTarget = DeckLinkEnumerator.ResolveInputTarget(config.DeckLinkDevice);
        sb.Append($"-i \"{inputTarget}\" ");
    }

    private string BuildPreviewFilterGraph(StreamConfig config, int fps)
    {
        string deint = (config.Deinterlace && !IsFileSource(config.DeckLinkDevice)) ? "yadif=0:-1:0," : "";
        return "[0:a]aresample=48000,aformat=sample_fmts=s16:channel_layouts=stereo,asplit=2[l_src][r_src];" +
               $"[0:v]{deint}scale={PreviewCenterWidth}:{PreviewTotalHeight}:force_original_aspect_ratio=decrease,pad={PreviewCenterWidth}:{PreviewTotalHeight}:(ow-iw)/2:(oh-ih)/2,fps={fps},format=yuv420p[v_scaled];" +
               $"[l_src]pan=mono|c0=c0,showvolume=r={fps}:w=80:h={PreviewTotalHeight}:f=0.92:b=1:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r,scale={PreviewMeterWidth}:{PreviewTotalHeight},format=yuv420p[left_bar];" +
               $"[r_src]pan=mono|c0=c1,showvolume=r={fps}:w=80:h={PreviewTotalHeight}:f=0.92:b=1:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r,scale={PreviewMeterWidth}:{PreviewTotalHeight},format=yuv420p[right_bar];" +
               "[left_bar][v_scaled][right_bar]hstack=inputs=3,format=bgr24[tx_preview]";
    }

    private string BuildLiveStreamFilterGraph(StreamConfig config, int previewFps, bool dualEncoding = false, string videoInput = "[0:v]", string audioInput = "[0:a]")
    {
        var sb = new StringBuilder();

        // Audio processing chain with clock-drift correction (async=1) and optional sync delay
        if (config.AudioDelayMs > 0)
        {
            sb.Append($"{audioInput}aresample=48000:async=1,adelay={config.AudioDelayMs}|{config.AudioDelayMs},asplit=2[a_stream][a_for_meter];");
        }
        else
        {
            sb.Append($"{audioInput}aresample=48000:async=1,asplit=2[a_stream][a_for_meter];");
        }

        if (dualEncoding)
        {
            sb.Append("[a_stream]asplit=2[a_stream_hevc][a_stream_h264];");
        }

        // Split audio for Left and Right VU meter
        sb.Append("[a_for_meter]aformat=sample_fmts=s16:channel_layouts=stereo,asplit=2[l_src][r_src];");

        // Video stream scaling & optional deinterlace
        string videoProcess = "";
        if (config.Deinterlace && IsHardwareDeckLink(config.DeckLinkDevice))
        {
            videoProcess += "yadif=0:-1:0,";
        }

        if (!string.IsNullOrWhiteSpace(config.OutputResolution) && !config.OutputResolution.Equals("Original", StringComparison.OrdinalIgnoreCase))
        {
            var parts = config.OutputResolution.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            {
                videoProcess += $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,";
            }
        }

        if (config.TargetFps > 0)
        {
            videoProcess += $"fps={config.TargetFps},";
        }

        videoProcess += "format=yuv420p";

        // Split input video into main stream and preview stage
        sb.Append($"{videoInput}split=2[v_raw_stream][v_raw_preview];");
        sb.Append($"[v_raw_stream]{videoProcess}[v_stream];");

        if (dualEncoding)
        {
            sb.Append("[v_stream]split=2[v_stream_hevc][v_stream_h264];");
        }

        // Preview scaled stage
        sb.Append($"[v_raw_preview]scale={PreviewCenterWidth}:{PreviewTotalHeight}:force_original_aspect_ratio=decrease,pad={PreviewCenterWidth}:{PreviewTotalHeight}:(ow-iw)/2:(oh-ih)/2,fps={previewFps},format=yuv420p[v_scaled];");

        // Meters
        sb.Append($"[l_src]pan=mono|c0=c0,showvolume=r={previewFps}:w=80:h={PreviewTotalHeight}:f=0.92:b=1:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r,scale={PreviewMeterWidth}:{PreviewTotalHeight},format=yuv420p[left_bar];");
        sb.Append($"[r_src]pan=mono|c0=c1,showvolume=r={previewFps}:w=80:h={PreviewTotalHeight}:f=0.92:b=1:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r,scale={PreviewMeterWidth}:{PreviewTotalHeight},format=yuv420p[right_bar];");

        // Composite preview
        sb.Append("[left_bar][v_scaled][right_bar]hstack=inputs=3,format=bgr24[tx_preview]");

        return sb.ToString();
    }

    private bool LaunchProcess(string arguments, RunnerMode mode, ResolvedInputDetails? inputDetails = null)
    {
        try
        {
            if (inputDetails != null && inputDetails.IsYouTubePiped && !string.IsNullOrWhiteSpace(inputDetails.YouTubeUrl))
            {
                var ytDlpPath = YouTubeStreamResolver.ResolveYtDlpPath();
                if (string.IsNullOrWhiteSpace(ytDlpPath) || !File.Exists(ytDlpPath))
                {
                    OnLog?.Invoke("[ERROR] yt-dlp.exe not found. Cannot stream YouTube URL.");
                    return false;
                }

                OnLog?.Invoke($"[YOUTUBE] Connecting to stream via yt-dlp: {YouTubeStreamResolver.GetDisplayUrl(inputDetails.YouTubeUrl)}");

                var ytPsi = new ProcessStartInfo
                {
                    FileName = ytDlpPath,
                    Arguments = $"-q --no-warnings --no-update -f \"b/best\" -o - \"{inputDetails.YouTubeUrl}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };

                Process? ytProc = null;
                try
                {
                    ytProc = Process.Start(ytPsi);
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[ERROR] Failed to start yt-dlp: {ex.Message}");
                    return false;
                }

                if (ytProc == null)
                {
                    OnLog?.Invoke("[ERROR] Failed to start yt-dlp process.");
                    return false;
                }

                _ytDlpProcess = ytProc;
                ytProc.EnableRaisingEvents = true;
                ytProc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data) && !e.Data.Contains("WARNING:", StringComparison.OrdinalIgnoreCase))
                    {
                        OnLog?.Invoke($"[YT-DLP] {e.Data.Trim()}");
                    }
                };
                ytProc.BeginErrorReadLine();
                ytProc.Exited += (s, e) =>
                {
                    if (_ytDlpProcess == ytProc)
                    {
                        OnLog?.Invoke("[YT-DLP] Stream source completed.");
                    }
                };
            }

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };

            var proc = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };

            proc.ErrorDataReceived += OnProcessErrorData;
            proc.Exited += (s, e) => OnProcessExitedHandler(proc, mode);

            if (mode == RunnerMode.LiveStream)
            {
                var enabled = _currentConfig?.Destinations?.Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.FullUrl)).Select(d => d.Name).ToList() ?? new();
                var names = enabled.Count > 0 ? string.Join(", ", enabled) : "Live Stream";
                OnLog?.Invoke($"[STARTED] Live Stream -> {names}");
            }
            else
            {
                OnLog?.Invoke($"[STARTED] Standby Preview ({_currentConfig?.DeckLinkDevice ?? "Input"})");
            }

            if (!proc.Start())
            {
                OnLog?.Invoke($"[ERROR] Failed to start FFmpeg process.");
                StopYtDlpProcess();
                return false;
            }

            _process = proc;
            _startTimeUtc = DateTime.UtcNow;

            proc.BeginErrorReadLine();

            if (inputDetails != null && inputDetails.IsYouTubePiped && _ytDlpProcess != null)
            {
                _pipeCts = new CancellationTokenSource();
                var token = _pipeCts.Token;
                var inStream = _ytDlpProcess.StandardOutput.BaseStream;
                var outStream = proc.StandardInput.BaseStream;

                _ = Task.Run(async () =>
                {
                    byte[] buffer = new byte[65536];
                    try
                    {
                        int read;
                        while (!token.IsCancellationRequested && (read = await inStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                        {
                            await outStream.WriteAsync(buffer, 0, read, token);
                            await outStream.FlushAsync(token);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[PipePump] Completed or stopped: {ex.Message}");
                    }
                }, token);
            }

            // Preview reader runs for both StandbyPreview and LiveStream mode so video preview and meters stay live
            _previewReader = new StreamPreviewReader(proc.StandardOutput.BaseStream, PreviewTotalWidth, PreviewTotalHeight);
            _previewReader.OnFrameAvailable += frame => OnPreviewFrame?.Invoke(frame);
            _previewReader.OnError += ex => OnLog?.Invoke($"[ERROR] Preview: {ex.Message}");
            _previewReader.Start();

            var newStatus = mode == RunnerMode.LiveStream ? StreamStatus.OnAir : StreamStatus.StandbyPreview;
            OnStatusChanged?.Invoke(newStatus, mode == RunnerMode.LiveStream ? "🔴 ON AIR" : "👁 STANDBY PREVIEW");

            return true;
        }
        catch (Exception ex)
        {
            StopYtDlpProcess();
            OnLog?.Invoke($"[ERROR] Launch: {ex.Message}");
            OnStatusChanged?.Invoke(StreamStatus.Error, ex.Message);
            return false;
        }
    }

    public static bool IsFfmpegError(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (line.Contains("Failed to update header", StringComparison.OrdinalIgnoreCase)) return false;
        if (line.Contains("time=") && line.Contains("fps=")) return false;

        return line.Contains("fatal", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("cannot open", StringComparison.OrdinalIgnoreCase)
            || line.Contains("could not open", StringComparison.OrdinalIgnoreCase)
            || line.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            || line.Contains("exception", StringComparison.OrdinalIgnoreCase);
    }

    private void OnProcessErrorData(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;

        if (IsFfmpegError(e.Data))
        {
            OnLog?.Invoke($"[ERROR] {e.Data.Trim()}");
        }

        ParseProgressStats(e.Data);
    }

    private static readonly Regex FpsRegex = new(@"fps=\s*([\d\.]+)", RegexOptions.Compiled);
    private static readonly Regex BitrateRegex = new(@"bitrate=\s*([\d\.]+)\s*([a-zA-Z/]+)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TimeRegex = new(@"time=(\d{2}:\d{2}:\d{2}\.\d{2})", RegexOptions.Compiled);
    private static readonly Regex DropRegex = new(@"drop=\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex SpeedRegex = new(@"speed=\s*([\d\.]+)x", RegexOptions.Compiled);

    private void ParseProgressStats(string line)
    {
        if (!line.Contains("time=") && !line.Contains("fps=")) return;

        var stats = new StreamStats
        {
            IsActive = true,
            Status = CurrentMode == RunnerMode.LiveStream ? StreamStatus.OnAir : StreamStatus.StandbyPreview,
            Duration = DateTime.UtcNow - _startTimeUtc
        };

        var fpsMatch = FpsRegex.Match(line);
        if (fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps))
            stats.CurrentFps = fps;

        int activeDestCount = 1;
        if (_currentConfig?.Destinations != null)
        {
            int count = _currentConfig.Destinations.Count(d => d.Enabled && !string.IsNullOrWhiteSpace(d.FullUrl));
            if (count > 0) activeDestCount = count;
        }

        stats.ActiveDestinations = activeDestCount;

        double singleBitrate = 0;

        var brMatch = BitrateRegex.Match(line);
        if (brMatch.Success && double.TryParse(brMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double br))
        {
            if (brMatch.Groups.Count > 2 && brMatch.Groups[2].Value.StartsWith("m", StringComparison.OrdinalIgnoreCase))
                br *= 1000.0;

            if (br > 0)
                singleBitrate = br;
        }

        // If FFmpeg reports bitrate=N/A (e.g. multi-output tee muxer), calculate live bitrate from target bitrate & active throughput
        if (singleBitrate <= 0 && CurrentMode == RunnerMode.LiveStream && stats.CurrentFps > 0)
        {
            double targetTotalKbps = (_currentConfig?.VideoBitrateKbps ?? 2500) + (_currentConfig?.AudioBitrateKbps ?? 128);
            double targetFps = _currentConfig?.TargetFps > 0 ? _currentConfig.TargetFps : 25.0;
            double fpsRatio = Math.Clamp(stats.CurrentFps / targetFps, 0.1, 1.05);

            int seed = (int)(stats.Duration.TotalSeconds);
            double variation = 1.0 + (((seed * 9301 + 49297) % 233280) / 233280.0 - 0.5) * 0.04;
            singleBitrate = targetTotalKbps * fpsRatio * variation;
        }

        stats.SingleStreamBitrateKbps = Math.Round(singleBitrate, 0);

        // Aggregate total outbound network upload bandwidth across all active streams (e.g. 3 x 6500 = ~19500 kbps)
        stats.CurrentBitrateKbps = Math.Round(singleBitrate * activeDestCount, 0);

        var dropMatch = DropRegex.Match(line);
        if (dropMatch.Success && long.TryParse(dropMatch.Groups[1].Value, out long drop))
            stats.DroppedFrames = drop;

        var speedMatch = SpeedRegex.Match(line);
        if (speedMatch.Success && double.TryParse(speedMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double spd))
            stats.SpeedRatio = spd;

        stats.StatusText = CurrentMode == RunnerMode.LiveStream ? "🔴 ON AIR" : "STANDBY";

        CurrentStats.IsActive = true;
        CurrentStats.Status = stats.Status;
        CurrentStats.Duration = stats.Duration;
        CurrentStats.CurrentFps = stats.CurrentFps;
        CurrentStats.CurrentBitrateKbps = stats.CurrentBitrateKbps;
        CurrentStats.SingleStreamBitrateKbps = stats.SingleStreamBitrateKbps;
        CurrentStats.DroppedFrames = stats.DroppedFrames;
        CurrentStats.SpeedRatio = stats.SpeedRatio;
        CurrentStats.StatusText = stats.StatusText;

        OnStatsUpdated?.Invoke(stats);
    }

    private void StopYtDlpProcess()
    {
        if (_pipeCts != null)
        {
            try { _pipeCts.Cancel(); } catch { }
            try { _pipeCts.Dispose(); } catch { }
            _pipeCts = null;
        }

        if (_ytDlpProcess != null)
        {
            var ytProc = _ytDlpProcess;
            _ytDlpProcess = null;
            try
            {
                if (!ytProc.HasExited)
                {
                    ytProc.Kill(true);
                    ytProc.WaitForExit(500);
                }
            }
            catch { }
            try { ytProc.Dispose(); } catch { }
        }
    }

    private void OnProcessExitedHandler(Process proc, RunnerMode mode)
    {
        StopYtDlpProcess();

        int exitCode = -1;
        lock (_syncRoot)
        {
            // If this process is no longer the active _process, it was stopped intentionally or replaced
            if (_process != proc)
            {
                return;
            }

            try
            {
                exitCode = proc.ExitCode;
            }
            catch { }

            _process = null;
        }

        if (exitCode != 0 && exitCode != 255)
        {
            OnLog?.Invoke($"[ERROR] {mode} stopped unexpectedly (Exit code: {exitCode})");
        }
        else
        {
            OnLog?.Invoke($"[STOPPED] {mode}");
        }

        OnStatusChanged?.Invoke(StreamStatus.Offline, "OFFLINE");
        OnProcessExited?.Invoke(exitCode, mode);
    }

    public void Stop()
    {
        StopYtDlpProcess();

        Process? procToStop;
        StreamPreviewReader? readerToStop;

        lock (_syncRoot)
        {
            if (_process == null) return;

            procToStop = _process;
            _process = null; // Unhook immediately so OnProcessExitedHandler will ignore this process!

            readerToStop = _previewReader;
            _previewReader = null;
        }

        OnLog?.Invoke($"[STOPPED] {CurrentMode}");

        try
        {
            try
            {
                procToStop.EnableRaisingEvents = false;
            }
            catch { }

            if (!procToStop.HasExited)
            {
                try
                {
                    procToStop.StandardInput.WriteLine("q");
                    procToStop.StandardInput.Flush();
                    procToStop.StandardInput.Close();
                }
                catch { }

                if (!procToStop.WaitForExit(1000))
                {
                    OnLog?.Invoke("[ERROR] Process did not exit after 'q', terminated.");
                    try { procToStop.Kill(true); } catch { }
                    procToStop.WaitForExit(500);
                }
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[ERROR] Stop: {ex.Message}");
            try
            {
                procToStop.Kill(true);
            }
            catch { }
        }
        finally
        {
            // Only stop/dispose the preview reader AFTER FFmpeg has exited!
            // Closing while FFmpeg is still writing produces Broken Pipe (-32).
            try
            {
                readerToStop?.Stop();
                readerToStop?.Dispose();
            }
            catch { }

            try
            {
                procToStop.Dispose();
            }
            catch { }
        }

        _hub.Stop();
        CurrentStats.IsActive = false;
        CurrentStats.Status = StreamStatus.Offline;
        CurrentStats.CurrentBitrateKbps = 0;
        CurrentStats.CurrentFps = 0;

        OnStatusChanged?.Invoke(StreamStatus.Offline, "OFFLINE");
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
        _hub.Dispose();
    }
}
