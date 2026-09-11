using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DeckLinkAPI;

namespace DeckLinkStreamStudio.Engines;

public sealed record DeckLinkDeviceEntry(string Name, string ModelName);

public static class DeckLinkEnumerator
{
    public const string DefaultFileSource = "go1080p25.mp4";

    public static List<DeckLinkDeviceEntry> GetInstalledDevices(string? ffmpegPath = null)
    {
        var devices = new List<DeckLinkDeviceEntry>();

        // File-based streaming source (looping go1080p25.mp4 in exe folder)
        devices.Add(new DeckLinkDeviceEntry(DefaultFileSource, "go1080p25.mp4 (Loop File)"));

        // 1. Try DeckLink COM SDK First
        try
        {
            var iterator = new CDeckLinkIteratorClass();
            while (true)
            {
                try
                {
                    iterator.Next(out var deckLink);
                    if (deckLink is null) break;

                    deckLink.GetDisplayName(out var displayName);
                    deckLink.GetModelName(out var modelName);
                    devices.Add(new DeckLinkDeviceEntry(displayName ?? "Unknown DeckLink", modelName ?? displayName ?? "DeckLink"));
                    Marshal.ReleaseComObject(deckLink);
                }
                catch (COMException)
                {
                    break;
                }
            }
            Marshal.ReleaseComObject(iterator);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeckLink COM Enumeration Notice] {ex.Message}");
        }

        if (devices.Count > 0)
        {
            return devices;
        }

        // 2. Fallback to FFmpeg querying if COM didn't return or failed
        if (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -f decklink -list_devices 1 -i dummy",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(3000);

                    using var reader = new StringReader(output);
                    string? line;
                    bool inDeviceSection = false;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Contains("Blackmagic DeckLink input devices:", StringComparison.OrdinalIgnoreCase))
                        {
                            inDeviceSection = true;
                            continue;
                        }

                        if (inDeviceSection)
                        {
                            var trimmed = line.Trim();
                            if (trimmed.StartsWith("'") && trimmed.EndsWith("'"))
                            {
                                var devName = trimmed.Trim('\'');
                                devices.Add(new DeckLinkDeviceEntry(devName, devName));
                            }
                            else if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("Error"))
                            {
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FFmpeg Enumeration Notice] {ex.Message}");
            }
        }

        // 3. Fallback defaults if physically offline or emulating
        if (devices.Count == 0)
        {
            devices.Add(new DeckLinkDeviceEntry("DeckLink Duo (1)", "DeckLink Duo (1)"));
            devices.Add(new DeckLinkDeviceEntry("DeckLink Duo (2)", "DeckLink Duo (2)"));
            devices.Add(new DeckLinkDeviceEntry("DeckLink Duo (3)", "DeckLink Duo (3)"));
            devices.Add(new DeckLinkDeviceEntry("DeckLink Duo (4)", "DeckLink Duo (4)"));
            devices.Add(new DeckLinkDeviceEntry("DeckLink SDI 4K", "DeckLink SDI 4K"));
        }

        return devices;
    }
}
