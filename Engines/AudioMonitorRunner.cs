using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

                    var matches = Regex.Matches(output, "\"([^\"]+)\"\\s*\\(audio");
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
                    var dshowAudio = ResolveDirectShowAudioDevice(decklinkDevice);
                    var ffplayPath = FfmpegStreamRunner.ResolveFfmpegPath().Replace("ffmpeg.exe", "ffplay.exe");
                    if (!File.Exists(ffplayPath))
                    {
                        ffplayPath = "ffplay.exe";
                    }

                    var arguments = $"-hide_banner -loglevel error -nostats -nodisp -f dshow -i \"audio={dshowAudio}\"";
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffplayPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    _process = Process.Start(psi);
                    if (_process == null) return false;

                    try
                    {
                        _process.PriorityClass = ProcessPriorityClass.AboveNormal;
                    }
                    catch { }

                    // Brief pause to verify process didn't immediately fail on device open
                    Thread.Sleep(300);
                    return !_process.HasExited;
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
