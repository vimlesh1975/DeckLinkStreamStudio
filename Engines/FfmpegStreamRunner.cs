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
    private StreamPreviewReader? _previewReader;
    private readonly object _syncRoot = new();
    private DateTime _startTimeUtc;
    private bool _isDisposed;
    private readonly string _ffmpegPath;

    public const int PreviewCenterWidth = 444;
    public const int PreviewTotalHeight = 250;
    public const int PreviewMeterWidth = 18;
    public const int PreviewTotalWidth = PreviewCenterWidth + (PreviewMeterWidth * 2); // 480 px

    public event Action<string>? OnLog;
    public event Action<StreamStats>? OnStatsUpdated;
    public event Action<System.Drawing.Bitmap>? OnPreviewFrame;
    public event Action<StreamStatus, string>? OnStatusChanged;
    public event Action<int>? OnProcessExited;

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

    public FfmpegStreamRunner(string? ffmpegPath = null)
    {
        _ffmpegPath = ResolveFfmpegPath(ffmpegPath);
    }

    public static string ResolveFfmpegPath(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            return customPath;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var localExe = Path.Combine(baseDir, "ffmpeg.exe");
        if (File.Exists(localExe))
            return localExe;

        var localToolsExe = Path.Combine(baseDir, "tools", "ffmpeg.exe");
        if (File.Exists(localToolsExe))
            return localToolsExe;

        var curToolsExe = Path.Combine(Directory.GetCurrentDirectory(), "tools", "ffmpeg.exe");
        if (File.Exists(curToolsExe))
            return curToolsExe;

        var newpToolsExe = @"d:\___newp\DeckLinkStreamStudio\tools\ffmpeg.exe";
        if (File.Exists(newpToolsExe))
            return newpToolsExe;

        var toolsExe = @"d:\_projects\streaming\tools\ffmpeg.exe";
        if (File.Exists(toolsExe))
            return toolsExe;

        var recorderExe = @"D:\_projects\FfmpegRecorder\bin\Debug\net10.0-windows\ffmpeg.exe";
        if (File.Exists(recorderExe))
            return recorderExe;

        var srtExe = @"D:\_projects\SrtSuite\bin\Release\net10.0-windows\win-x64\ffmpeg.exe";
        if (File.Exists(srtExe))
            return srtExe;

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

            CurrentMode = RunnerMode.StandbyPreview;
            var args = BuildStandbyPreviewArguments(config);
            return LaunchProcess(args, RunnerMode.StandbyPreview);
        }
    }

    public bool StartLiveStream(StreamConfig config)
    {
        lock (_syncRoot)
        {
            if (IsRunning)
            {
                Stop();
            }

            CurrentMode = RunnerMode.LiveStream;
            var args = BuildLiveStreamArguments(config);
            return LaunchProcess(args, RunnerMode.LiveStream);
        }
    }

    public static bool? _isNvencSupported;
    public static bool IsNvencAvailable()
    {
        if (_isNvencSupported.HasValue) return _isNvencSupported.Value;
        try
        {
            var ffmpeg = ResolveFfmpegPath();
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -f lavfi -i testsrc=duration=1:size=64x64:rate=1 -c:v h264_nvenc -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(1500);
                _isNvencSupported = proc.ExitCode == 0;
                return _isNvencSupported.Value;
            }
        }
        catch { }
        _isNvencSupported = false;
        return false;
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
        if (IsFileSource(config.DeckLinkDevice))
        {
            var filePath = ResolveMediaFilePath(config.DeckLinkDevice);
            sb.Append($"-stream_loop -1 -re -i \"{filePath}\" ");
        }
        else
        {
            AppendDeckLinkInput(sb, config);
        }
    }

    private string BuildStandbyPreviewArguments(StreamConfig config)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel warning ");

        // Input Source (File loop or DeckLink hardware)
        AppendInputSource(sb, config);

        // Preview Filter: 16:9 scaled video + left/right audio VU meters
        var filter = BuildPreviewFilterGraph(config, config.PreviewFps > 0 ? config.PreviewFps : 15);
        sb.Append($"-filter_complex \"{filter}\" ");

        // Map preview to stdout pipe as raw bgr24 video
        sb.Append("-map \"[tx_preview]\" -f rawvideo -pix_fmt bgr24 pipe:1");

        return sb.ToString();
    }

    private string BuildLiveStreamArguments(StreamConfig config)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel info -stats ");

        // Input Source (File loop or DeckLink hardware)
        AppendInputSource(sb, config);

        // Filter Complex for Preview + Deinterlacing / Scaling
        var previewFps = config.PreviewFps > 0 ? config.PreviewFps : 15;
        var filter = BuildLiveStreamFilterGraph(config, previewFps);
        sb.Append($"-filter_complex \"{filter}\" ");

        // Video and Audio Encoding settings
        var gopSize = Math.Max(25, (config.TargetFps > 0 ? config.TargetFps : 25) * config.KeyframeIntervalSeconds);
        var videoBitrate = $"{config.VideoBitrateKbps}k";
        var maxRate = $"{config.VideoBitrateKbps}k";
        var bufSize = $"{config.VideoBitrateKbps * 2}k";

        var encoder = config.VideoEncoder;
        if ((encoder == VideoEncoderType.H264_NVENC || encoder == VideoEncoderType.HEVC_NVENC) && !IsNvencAvailable())
        {
            encoder = VideoEncoderType.LibX264;
        }

        // Video Encoder
        string videoCodecArgs;
        switch (encoder)
        {
            case VideoEncoderType.H264_NVENC:
                videoCodecArgs = $"-c:v h264_nvenc -preset ll -tune ll -zerolatency 1 -g {gopSize} -bf 0 -b:v {videoBitrate} -maxrate {maxRate} -bufsize {bufSize} -pix_fmt yuv420p";
                break;
            case VideoEncoderType.HEVC_NVENC:
                videoCodecArgs = $"-c:v hevc_nvenc -preset ll -tune ll -zerolatency 1 -g {gopSize} -bf 0 -b:v {videoBitrate} -maxrate {maxRate} -bufsize {bufSize} -pix_fmt yuv420p";
                break;
            case VideoEncoderType.LibX264:
            default:
                videoCodecArgs = $"-c:v libx264 -preset veryfast -tune zerolatency -g {gopSize} -bf 0 -b:v {videoBitrate} -maxrate {maxRate} -bufsize {bufSize} -pix_fmt yuv420p";
                break;
        }

        // Audio Codec
        var audioBitrate = $"{config.AudioBitrateKbps}k";
        var audioCodecArgs = $"-c:a aac -b:a {audioBitrate} -ar 48000 -ac 2";

        // Collect Enabled Destinations (Sahyadri Facebook, Sahyadri YouTube, Sahyadri YouTube News)
        var activeTargets = new List<string>();
        foreach (var dest in config.Destinations)
        {
            if (dest.Enabled && !string.IsNullOrWhiteSpace(dest.FullUrl))
            {
                activeTargets.Add($"[f=flv:onfail=ignore]{dest.FullUrl}");
            }
        }

        // Broadcast Outputs via Tee Muxer
        if (activeTargets.Count > 0)
        {
            var teeChain = string.Join("|", activeTargets);
            sb.Append($"-map \"[v_stream]\" -map \"[a_stream]\" {videoCodecArgs} {audioCodecArgs} -max_muxing_queue_size 4096 -f tee \"{teeChain}\" ");
        }

        // Output preview to pipe:1 for in-app operator monitor
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
        sb.Append($"-i \"{config.DeckLinkDevice}\" ");
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

    private string BuildLiveStreamFilterGraph(StreamConfig config, int previewFps)
    {
        var sb = new StringBuilder();

        // Audio processing chain with optional sync delay
        if (config.AudioDelayMs > 0)
        {
            sb.Append($"[0:a]aresample=48000,adelay={config.AudioDelayMs}|{config.AudioDelayMs},asplit=2[a_stream][a_for_meter];");
        }
        else
        {
            sb.Append("[0:a]aresample=48000,asplit=2[a_stream][a_for_meter];");
        }

        // Split audio for Left and Right VU meter
        sb.Append("[a_for_meter]aformat=sample_fmts=s16:channel_layouts=stereo,asplit=2[l_src][r_src];");

        // Video stream scaling & optional deinterlace
        string videoProcess = "";
        if (config.Deinterlace && !IsFileSource(config.DeckLinkDevice))
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
        sb.Append($"[0:v]split=2[v_raw_stream][v_raw_preview];");
        sb.Append($"[v_raw_stream]{videoProcess}[v_stream];");

        // Preview scaled stage
        sb.Append($"[v_raw_preview]scale={PreviewCenterWidth}:{PreviewTotalHeight}:force_original_aspect_ratio=decrease,pad={PreviewCenterWidth}:{PreviewTotalHeight}:(ow-iw)/2:(oh-ih)/2,fps={previewFps},format=yuv420p[v_scaled];");

        // Meters
        sb.Append($"[l_src]pan=mono|c0=c0,showvolume=r={previewFps}:w=80:h={PreviewTotalHeight}:f=0.92:b=1:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r,scale={PreviewMeterWidth}:{PreviewTotalHeight},format=yuv420p[left_bar];");
        sb.Append($"[r_src]pan=mono|c0=c1,showvolume=r={previewFps}:w=80:h={PreviewTotalHeight}:f=0.92:b=1:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r,scale={PreviewMeterWidth}:{PreviewTotalHeight},format=yuv420p[right_bar];");

        // Composite preview
        sb.Append("[left_bar][v_scaled][right_bar]hstack=inputs=3,format=bgr24[tx_preview]");

        return sb.ToString();
    }

    private bool LaunchProcess(string arguments, RunnerMode mode)
    {
        try
        {
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
            proc.Exited += OnProcessExitedHandler;

            OnLog?.Invoke($"[START {mode}] {_ffmpegPath} {arguments}");

            if (!proc.Start())
            {
                OnLog?.Invoke($"[ERROR] Failed to start FFmpeg process.");
                return false;
            }

            _process = proc;
            _startTimeUtc = DateTime.UtcNow;

            proc.BeginErrorReadLine();

            // Start reading preview frames from stdout (848x450 bgr24)
            _previewReader = new StreamPreviewReader(proc.StandardOutput.BaseStream, PreviewTotalWidth, PreviewTotalHeight);
            _previewReader.OnFrameAvailable += frame => OnPreviewFrame?.Invoke(frame);
            _previewReader.OnError += ex => OnLog?.Invoke($"[Preview Error] {ex.Message}");
            _previewReader.Start();

            var newStatus = mode == RunnerMode.LiveStream ? StreamStatus.OnAir : StreamStatus.StandbyPreview;
            OnStatusChanged?.Invoke(newStatus, mode == RunnerMode.LiveStream ? "🔴 ON AIR" : "👁 STANDBY PREVIEW");

            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[Launch Exception] {ex.Message}");
            OnStatusChanged?.Invoke(StreamStatus.Error, ex.Message);
            return false;
        }
    }

    private void OnProcessErrorData(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;

        OnLog?.Invoke(e.Data);
        ParseProgressStats(e.Data);
    }

    private static readonly Regex FpsRegex = new(@"fps=\s*([\d\.]+)", RegexOptions.Compiled);
    private static readonly Regex BitrateRegex = new(@"bitrate=\s*([\d\.]+)kbits/s", RegexOptions.Compiled);
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

        var brMatch = BitrateRegex.Match(line);
        if (brMatch.Success && double.TryParse(brMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double br))
            stats.CurrentBitrateKbps = br;

        var dropMatch = DropRegex.Match(line);
        if (dropMatch.Success && long.TryParse(dropMatch.Groups[1].Value, out long drop))
            stats.DroppedFrames = drop;

        var speedMatch = SpeedRegex.Match(line);
        if (speedMatch.Success && double.TryParse(speedMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double spd))
            stats.SpeedRatio = spd;

        stats.StatusText = CurrentMode == RunnerMode.LiveStream ? "🔴 ON AIR" : "STANDBY";

        OnStatsUpdated?.Invoke(stats);
    }

    private void OnProcessExitedHandler(object? sender, EventArgs e)
    {
        int exitCode = -1;
        try
        {
            exitCode = _process?.ExitCode ?? -1;
        }
        catch { }

        OnLog?.Invoke($"[Process Exited] Exit code: {exitCode}");
        OnStatusChanged?.Invoke(StreamStatus.Offline, "OFFLINE");
        OnProcessExited?.Invoke(exitCode);
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            if (_process == null) return;

            try
            {
                _previewReader?.Stop();
                _previewReader?.Dispose();
                _previewReader = null;

                if (!_process.HasExited)
                {
                    try
                    {
                        _process.StandardInput.WriteLine("q");
                        _process.StandardInput.Flush();
                    }
                    catch { }

                    if (!_process.WaitForExit(2500))
                    {
                        OnLog?.Invoke("[Stop] FFmpeg process did not exit after 'q'. Killing tree.");
                        _process.Kill(true);
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Stop Exception] {ex.Message}");
                try
                {
                    _process.Kill(true);
                }
                catch { }
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }

            OnStatusChanged?.Invoke(StreamStatus.Offline, "OFFLINE");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}
