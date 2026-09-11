using System;
using System.Diagnostics;
using System.IO;

namespace DeckLinkStreamStudio.Engines;

public sealed class AudioMonitorRunner : IDisposable
{
    private Process? _process;
    private readonly object _syncRoot = new();

    public bool IsMonitoring
    {
        get
        {
            lock (_syncRoot)
            {
                return _process != null && !_process.HasExited;
            }
        }
    }

    public bool Start(string decklinkDevice, string formatCode = "Hi50")
    {
        lock (_syncRoot)
        {
            Stop();

            var ffplayPath = FfmpegStreamRunner.ResolveFfmpegPath().Replace("ffmpeg.exe", "ffplay.exe");
            if (!File.Exists(ffplayPath))
            {
                ffplayPath = "ffplay.exe";
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffplayPath,
                    Arguments = $"-nodisp -f decklink -audio_input embedded -channels 2 -i \"{decklinkDevice}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _process = Process.Start(psi);
                return _process != null && !_process.HasExited;
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
            if (_process == null) return;

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(true);
                }
            }
            catch { }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
