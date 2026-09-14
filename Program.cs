using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using DeckLinkStreamStudio.Forms;

namespace DeckLinkStreamStudio;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--list-devices" || args[0] == "-l"))
        {
            AttachConsole(-1);
            var devList = DeckLinkStreamStudio.Engines.DeckLinkEnumerator.GetInstalledDevices(forceRefresh: true);
            foreach (var d in devList)
            {
                Console.WriteLine($"[DEVICE] Name='{d.Name}' | DeviceId='{d.DeviceId}' | Audio='{d.DshowAudioName}'");
            }
            return;
        }

        bool isOnlyInstance = false;
        try
        {
            const string mutexName = "DeckLinkStreamStudio_SingleInstance_Mutex_v3";
            _singleInstanceMutex = new Mutex(true, mutexName, out isOnlyInstance);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Mutex Error: {ex.Message}");
            return;
        }

        if (!isOnlyInstance)
        {
            MessageBox.Show("Another instance is already running.");
            return;
        }

        try
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
            catch (Exception ex)
            {
                File.WriteAllText("crash.log", ex.ToString());
                MessageBox.Show($"UI Crash: {ex.Message}");
            }
            finally
            {
                StopBundledHelperProcesses();
            }
        }
        finally
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch { }
            _singleInstanceMutex.Dispose();
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
