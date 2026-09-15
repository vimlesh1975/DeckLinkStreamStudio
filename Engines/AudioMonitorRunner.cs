using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DeckLinkStreamStudio.Engines;

public sealed class AudioMonitorRunner : IDisposable
{
    private Process? _process;
    private System.Windows.Media.MediaPlayer? _mediaPlayer;
    private bool _isFileMonitoring;
    private readonly object _syncRoot = new();
    private static List<string>? _cachedDshowAudioDevices;
    private static readonly object _dshowLock = new();

    /// <summary>Raised on the thread-pool with diagnostic and error messages from the listen process.</summary>
    public event Action<string>? OnLog;

    public bool IsMonitoring
    {
        get
        {
            lock (_syncRoot)
            {
                return _isFileMonitoring || (_process != null && !_process.HasExited);
            }
        }
    }

    public static List<string> GetDirectShowAudioDevices()
    {
        lock (_dshowLock)
        {
            if (_cachedDshowAudioDevices != null)
                return _cachedDshowAudioDevices;

            var list = new List<string>();
            try
            {
                var ffmpegPath = FfmpegStreamRunner.ResolveFfmpegPath();
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(1500);

                    // Match only pure audio devices: ends with (audio) not (audio, video)
                    var matches = Regex.Matches(output, "\"([^\"]+)\"\\s*\\(audio\\)");
                    foreach (Match m in matches)
                    {
                        if (m.Success && m.Groups.Count > 1)
                        {
                            var name = m.Groups[1].Value.Trim();
                            if (!string.IsNullOrWhiteSpace(name) && !list.Contains(name))
                            {
                                list.Add(name);
                            }
                        }
                    }
                }
            }
            catch { }

            _cachedDshowAudioDevices = list;
            return list;
        }
    }

    public static string ResolveDirectShowAudioDevice(string decklinkDevice)
    {
        if (string.IsNullOrWhiteSpace(decklinkDevice))
            return "Line In (Blackmagic DeckLink SDI 4K Audio)";

        var devices = GetDirectShowAudioDevices();
        foreach (var dev in devices)
        {
            if (dev.Contains(decklinkDevice, StringComparison.OrdinalIgnoreCase))
                return dev;
        }

        if (decklinkDevice.StartsWith("Blackmagic", StringComparison.OrdinalIgnoreCase))
            return $"Line In ({decklinkDevice} Audio)";

        return $"Line In (Blackmagic {decklinkDevice} Audio)";
    }

    public bool Start(string decklinkDevice, string formatCode = "Hi50")
    {
        lock (_syncRoot)
        {
            Stop();

            try
            {
                if (FfmpegStreamRunner.IsFileSource(decklinkDevice))
                {
                    var filePath = FfmpegStreamRunner.ResolveMediaFilePath(decklinkDevice);
                    if (!File.Exists(filePath)) return false;

                    // Use native Windows Media Foundation MediaPlayer for pristine, hardware-buffered audio playback.
                    // This completely eliminates ffplay SDL2 buffer underruns, container seek lag, and distorted sound.
                    var player = new System.Windows.Media.MediaPlayer();
                    player.Open(new Uri(filePath));
                    player.Volume = 1.0;
                    player.MediaEnded += (s, e) =>
                    {
                        try
                        {
                            if (_isFileMonitoring && _mediaPlayer != null)
                            {
                                _mediaPlayer.Position = TimeSpan.Zero;
                                _mediaPlayer.Play();
                            }
                        }
                        catch { }
                    };
                    player.MediaFailed += (s, e) =>
                    {
                        lock (_syncRoot)
                        {
                            _isFileMonitoring = false;
                        }
                    };
                    player.Play();
                    _mediaPlayer = player;
                    _isFileMonitoring = true;
                    return true;
                }
                else
                {
                    // DeckLink hardware cards expose shared DirectShow audio capture endpoints.
                    // This allows listening to the SDI embedded audio without interfering with the
                    // main FFmpeg DeckLink video/audio hardware capture lock.
                    var dshowAudio = DeckLinkEnumerator.ResolveAudioTarget(decklinkDevice);
                    if (string.IsNullOrWhiteSpace(dshowAudio))
                    {
                        dshowAudio = ResolveDirectShowAudioDevice(decklinkDevice);
                    }
                    var ffplayPath = FfmpegStreamRunner.ResolveFfmpegPath().Replace("ffmpeg.exe", "ffplay.exe");
                    bool ffplayMissing = !File.Exists(ffplayPath);
                    if (ffplayMissing)
                    {
                        ffplayPath = "ffplay.exe"; // last-resort: hope it is on PATH
                    }

                    OnLog?.Invoke($"[LISTEN] ffplay path: {ffplayPath}{(ffplayMissing ? " (WARNING: file not found at resolved path — trying PATH)" : "")}");
                    OnLog?.Invoke($"[LISTEN] DirectShow audio device: \"{dshowAudio}\"");

                    // Use verbose loglevel so device-open errors are captured in stderr
                    var arguments = $"-hide_banner -loglevel verbose -nostats -nodisp -f dshow -i \"audio={dshowAudio}\"";
                    var errorOutput = new StringBuilder();
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffplayPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    };

                    _process = Process.Start(psi);
                    if (_process == null)
                    {
                        OnLog?.Invoke("[LISTEN ERROR] Failed to start ffplay process (Process.Start returned null).");
                        return false;
                    }

                    // Capture stderr asynchronously so it doesn't block the read pipe
                    _process.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                            errorOutput.AppendLine(e.Data);
                    };
                    _process.BeginErrorReadLine();

                    try
                    {
                        _process.PriorityClass = ProcessPriorityClass.AboveNormal;
                    }
                    catch { }

                    // Wait a bit longer (500 ms) to give ffplay time to open the device
                    Thread.Sleep(500);

                    if (_process.HasExited)
                    {
                        var err = errorOutput.ToString().Trim();
                        OnLog?.Invoke($"[LISTEN ERROR] ffplay exited immediately (code {_process.ExitCode}).");
                        if (!string.IsNullOrWhiteSpace(err))
                        {
                            foreach (var line in err.Split('\n'))
                            {
                                var trimmed = line.Trim('\r', '\n', ' ');
                                if (!string.IsNullOrWhiteSpace(trimmed))
                                    OnLog?.Invoke($"[LISTEN] ffplay: {trimmed}");
                            }
                        }
                        else
                        {
                            OnLog?.Invoke("[LISTEN] ffplay produced no error output — check that ffplay.exe is a full build and the device name is correct.");
                        }
                        return false;
                    }

                    OnLog?.Invoke($"[LISTEN] ffplay running (PID {_process.Id})");
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            _isFileMonitoring = false;
            if (_mediaPlayer != null)
            {
                try
                {
                    var player = _mediaPlayer;
                    _mediaPlayer = null;
                    if (player.Dispatcher.CheckAccess())
                    {
                        player.Stop();
                        player.Close();
                    }
                    else
                    {
                        player.Dispatcher.Invoke(() =>
                        {
                            try { player.Stop(); player.Close(); } catch { }
                        });
                    }
                }
                catch { }
            }

            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(true);
                        _process.WaitForExit(500);
                    }
                }
                catch { }
                finally
                {
                    try { _process.Dispose(); } catch { }
                    _process = null;
                }
            }

            KillOrphanedFfplayProcesses();
        }
    }

    private static void KillOrphanedFfplayProcesses()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("ffplay"))
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill(true);
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
    }
}
