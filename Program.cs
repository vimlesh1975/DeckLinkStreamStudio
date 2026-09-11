using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using DeckLinkStreamStudio.Forms;

namespace DeckLinkStreamStudio;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Cleanup any stale helper processes from previous abnormal terminations
        StopBundledHelperProcesses();

        AppDomain.CurrentDomain.ProcessExit += (s, e) => StopBundledHelperProcesses();
        Application.ApplicationExit += (s, e) => StopBundledHelperProcesses();

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Application.Run(new MainForm());
        }
        finally
        {
            StopBundledHelperProcesses();
        }
    }

    public static void StopBundledHelperProcesses()
    {
        try
        {
            var appDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ffmpeg",
                "ffplay",
                "ffprobe"
            };

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (!targets.Contains(proc.ProcessName)) continue;

                    var modPath = proc.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(modPath)) continue;

                    var modDir = Path.GetDirectoryName(Path.GetFullPath(modPath))?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(modDir, appDir, StringComparison.OrdinalIgnoreCase) && !proc.HasExited)
                    {
                        proc.Kill(true);
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch { }
    }
}
