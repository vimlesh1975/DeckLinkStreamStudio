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
        var deckLinkCards = new List<DeckLinkDeviceEntry>();

        ffmpegPath ??= FfmpegStreamRunner.ResolveFfmpegPath();

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
                    var name = displayName ?? modelName ?? "DeckLink";
                    if (!deckLinkCards.Exists(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        deckLinkCards.Add(new DeckLinkDeviceEntry(name, modelName ?? name));
                    }
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

        // 2. Fallback to FFmpeg querying if COM didn't find any hardware cards
        if (deckLinkCards.Count == 0 && !string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
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
                            if (line.Contains("Error opening input", StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }

                            int firstQuote = line.IndexOf('\'');
                            int lastQuote = line.LastIndexOf('\'');
                            if (firstQuote >= 0 && lastQuote > firstQuote)
                            {
                                var devName = line.Substring(firstQuote + 1, lastQuote - firstQuote - 1).Trim();
                                if (!string.IsNullOrWhiteSpace(devName) &&
                                    !deckLinkCards.Exists(d => d.Name.Equals(devName, StringComparison.OrdinalIgnoreCase)))
                                {
                                    deckLinkCards.Add(new DeckLinkDeviceEntry(devName, devName));
                                }
                            }
                            else if (string.IsNullOrWhiteSpace(line.Trim()))
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
        if (deckLinkCards.Count == 0)
        {
            deckLinkCards.Add(new DeckLinkDeviceEntry("DeckLink Duo (1)", "DeckLink Duo (1)"));
            deckLinkCards.Add(new DeckLinkDeviceEntry("DeckLink Duo (2)", "DeckLink Duo (2)"));
            deckLinkCards.Add(new DeckLinkDeviceEntry("DeckLink Duo (3)", "DeckLink Duo (3)"));
            deckLinkCards.Add(new DeckLinkDeviceEntry("DeckLink Duo (4)", "DeckLink Duo (4)"));
            deckLinkCards.Add(new DeckLinkDeviceEntry("DeckLink SDI 4K", "DeckLink SDI 4K"));
        }

        // Add DeckLink hardware cards
        devices.AddRange(deckLinkCards);

        // Always keep the file-based streaming source as standby/loop source
        devices.Add(new DeckLinkDeviceEntry(DefaultFileSource, "go1080p25.mp4 (Loop File)"));

        return devices;
    }
}
