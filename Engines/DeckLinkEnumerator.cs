using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DeckLinkAPI;

namespace DeckLinkStreamStudio.Engines;

public sealed record DeckLinkDeviceEntry(
    string Name,
    string DeviceId,
    string ModelName,
    string DshowAudioName = ""
)
{
    public override string ToString() => Name;
}

public static class DeckLinkEnumerator
{
    public const string DefaultFileSource = "go1080p25.mp4";

    private static readonly object _syncLock = new();
    private static List<DeckLinkDeviceEntry>? _cachedDevices;

    public static List<DeckLinkDeviceEntry> GetInstalledDevices(string? ffmpegPath = null, bool forceRefresh = false)
    {
        lock (_syncLock)
        {
            if (_cachedDevices != null && !forceRefresh)
            {
                return new List<DeckLinkDeviceEntry>(_cachedDevices);
            }

            var devices = new List<DeckLinkDeviceEntry>();
            var deckLinkCards = new List<DeckLinkDeviceEntry>();

            ffmpegPath ??= FfmpegStreamRunner.ResolveFfmpegPath();

            // 1. Primary discovery: query FFmpeg -sources decklink
            // This accurately detects ALL sub-devices across multiple physical cards
            // and provides the exact hardware device identifiers required by FFmpeg.
            if (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = "-hide_banner -sources decklink",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        var stdout = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(3000);

                        var rawEntries = ParseFfmpegSources(stdout);
                        if (rawEntries.Count > 0)
                        {
                            deckLinkCards.AddRange(BuildEntriesFromFfmpegSources(rawEntries));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DeckLink FFmpeg Sources Enumeration Notice] {ex.Message}");
                }
            }

            // 2. Secondary fallback: DeckLink COM SDK
            if (deckLinkCards.Count == 0)
            {
                try
                {
                    var iterator = new CDeckLinkIteratorClass();
                    var rawComList = new List<(string name, string model)>();
                    while (true)
                    {
                        try
                        {
                            iterator.Next(out var deckLink);
                            if (deckLink is null) break;

                            deckLink.GetDisplayName(out var displayName);
                            deckLink.GetModelName(out var modelName);
                            var name = displayName ?? modelName ?? "DeckLink";
                            rawComList.Add((name, modelName ?? name));
                            Marshal.ReleaseComObject(deckLink);
                        }
                        catch (COMException)
                        {
                            break;
                        }
                    }
                    Marshal.ReleaseComObject(iterator);

                    if (rawComList.Count > 0)
                    {
                        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var item in rawComList)
                        {
                            nameCounts[item.name] = nameCounts.GetValueOrDefault(item.name, 0) + 1;
                        }

                        bool hasDuplicates = nameCounts.Values.Any(c => c > 1);
                        var currentOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                        foreach (var (name, model) in rawComList)
                        {
                            int occ = currentOccurrences[name] = currentOccurrences.GetValueOrDefault(name, 0) + 1;
                            string entryName = hasDuplicates ? $"Card {occ} - {name}" : name;
                            deckLinkCards.Add(new DeckLinkDeviceEntry(entryName, name, model));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DeckLink COM Enumeration Notice] {ex.Message}");
                }
            }

            // 3. Fallback defaults if physically offline or emulating
            if (deckLinkCards.Count == 0)
            {
                deckLinkCards.Add(new DeckLinkDeviceEntry("DeckLink Duo (1)", "DeckLink Duo (1)", "DeckLink Duo (1)"));
                deckLinkCards.Add(new DeckLinkDeviceEntry("DeckLink Duo (2)", "DeckLink Duo (2)", "DeckLink Duo (2)"));
                deckLinkCards.Add(new DeckLinkDeviceEntry("DeckLink Duo (3)", "DeckLink Duo (3)", "DeckLink Duo (3)"));
                deckLinkCards.Add(new DeckLinkDeviceEntry("DeckLink Duo (4)", "DeckLink Duo (4)", "DeckLink Duo (4)"));
                deckLinkCards.Add(new DeckLinkDeviceEntry("DeckLink SDI 4K", "DeckLink SDI 4K", "DeckLink SDI 4K"));
            }

            devices.AddRange(deckLinkCards);

            // Always keep the file-based streaming source as standby/loop source
            devices.Add(new DeckLinkDeviceEntry(DefaultFileSource, DefaultFileSource, "go1080p25.mp4 (Loop File)"));

            _cachedDevices = devices;
            return new List<DeckLinkDeviceEntry>(devices);
        }
    }

    public static string ResolveInputTarget(string? deviceNameOrId)
    {
        if (string.IsNullOrWhiteSpace(deviceNameOrId) || FfmpegStreamRunner.IsFileSource(deviceNameOrId))
            return deviceNameOrId ?? "";

        var devices = GetInstalledDevices();

        // 1. Direct match by Name
        var match = devices.FirstOrDefault(d => d.Name.Equals(deviceNameOrId, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match.DeviceId;

        // 2. Direct match by DeviceId
        match = devices.FirstOrDefault(d => d.DeviceId.Equals(deviceNameOrId, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match.DeviceId;

        // 3. EndsWith match (e.g. "DeckLink Duo (1)" matches "Card 1 - DeckLink Duo (1)")
        match = devices.FirstOrDefault(d => d.Name.EndsWith(deviceNameOrId, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match.DeviceId;

        match = devices.FirstOrDefault(d => d.Name.Contains(deviceNameOrId, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match.DeviceId;

        return deviceNameOrId;
    }

    public static string ResolveAudioTarget(string? deviceNameOrId)
    {
        if (string.IsNullOrWhiteSpace(deviceNameOrId) || FfmpegStreamRunner.IsFileSource(deviceNameOrId))
            return "";

        var devices = GetInstalledDevices();
        var match = devices.FirstOrDefault(d => 
            d.Name.Equals(deviceNameOrId, StringComparison.OrdinalIgnoreCase) || 
            d.DeviceId.Equals(deviceNameOrId, StringComparison.OrdinalIgnoreCase) ||
            d.Name.EndsWith(deviceNameOrId, StringComparison.OrdinalIgnoreCase));

        if (match != null && !string.IsNullOrWhiteSpace(match.DshowAudioName))
            return match.DshowAudioName;

        return AudioMonitorRunner.ResolveDirectShowAudioDevice(deviceNameOrId);
    }

    private static List<(string id, string name)> ParseFfmpegSources(string stdout)
    {
        var list = new List<(string id, string name)>();
        var regex = new Regex(@"^\s*(?<id>[0-9a-fA-F:]+)\s+\[(?<name>[^\]]+)\]", RegexOptions.Multiline);
        var matches = regex.Matches(stdout);
        foreach (Match m in matches)
        {
            var id = m.Groups["id"].Value.Trim();
            var name = m.Groups["name"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            {
                list.Add((id, name));
            }
        }
        return list;
    }

    private static List<DeckLinkDeviceEntry> BuildEntriesFromFfmpegSources(List<(string id, string name)> rawEntries)
    {
        var result = new List<DeckLinkDeviceEntry>();
        var dshowAudioList = AudioMonitorRunner.GetDirectShowAudioDevices();

        // Group by card prefix from ID (e.g. "81:488d9ef0:00000000" -> card key "81:488d9ef")
        var cardGroups = new List<List<(string id, string name)>>();
        string currentCardKey = "";
        List<(string id, string name)>? currentGroup = null;

        foreach (var entry in rawEntries)
        {
            string cardKey = ExtractCardKey(entry.id);
            if (currentGroup == null || cardKey != currentCardKey)
            {
                currentCardKey = cardKey;
                currentGroup = new List<(string id, string name)>();
                cardGroups.Add(currentGroup);
            }
            currentGroup.Add(entry);
        }

        bool hasMultipleCards = cardGroups.Count > 1;

        for (int cardIdx = 0; cardIdx < cardGroups.Count; cardIdx++)
        {
            var group = cardGroups[cardIdx];
            int cardNumber = cardIdx + 1;

            for (int subIdx = 0; subIdx < group.Count; subIdx++)
            {
                var item = group[subIdx];
                int channelNumber = ExtractChannelNumber(item.name, subIdx + 1);

                string friendlyName = hasMultipleCards
                    ? $"Card {cardNumber} - {item.name}"
                    : item.name;

                string dshowAudio = FindDirectShowAudio(dshowAudioList, cardNumber, channelNumber, item.name);

                result.Add(new DeckLinkDeviceEntry(friendlyName, item.id, item.name, dshowAudio));
            }
        }

        return result;
    }

    private static string ExtractCardKey(string id)
    {
        var parts = id.Split(':');
        if (parts.Length >= 2 && parts[1].Length >= 2)
        {
            return $"{parts[0]}:{parts[1].Substring(0, parts[1].Length - 1)}";
        }
        return id;
    }

    private static int ExtractChannelNumber(string name, int fallback)
    {
        var match = Regex.Match(name, @"\((\d+)\)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int ch))
        {
            return ch;
        }
        return fallback;
    }

    private static string FindDirectShowAudio(List<string> dshowList, int cardNumber, int channelNumber, string cardModelName = "")
    {
        if (dshowList == null || dshowList.Count == 0) return "";

        // 1. Direct keyword match using the card model name — most reliable.
        //    e.g. "DeckLink SDI 4K" → "Line In (Blackmagic DeckLink SDI 4K Audio)"
        //         "DeckLink Duo 2 (1)" → "Line In (Blackmagic DeckLink Duo 2 (1) Audio)"
        if (!string.IsNullOrWhiteSpace(cardModelName))
        {
            var nameMatch = dshowList.FirstOrDefault(d =>
                d.Contains(cardModelName, StringComparison.OrdinalIgnoreCase));
            if (nameMatch != null) return nameMatch;
        }

        // 2. Position-based fallback for multi-card/multi-channel scenarios
        string chPattern = $"({channelNumber})";

        if (cardNumber == 1)
        {
            var match = dshowList.FirstOrDefault(d => 
                d.Contains(chPattern, StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(d, @"Line In \(\d+-", RegexOptions.IgnoreCase));
            if (match != null) return match;
        }
        else
        {
            string cardPrefix = $"Line In ({cardNumber}-";
            var match = dshowList.FirstOrDefault(d => 
                d.StartsWith(cardPrefix, StringComparison.OrdinalIgnoreCase) &&
                d.Contains(chPattern, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        var fallbackMatch = dshowList.FirstOrDefault(d => d.Contains(chPattern, StringComparison.OrdinalIgnoreCase));
        return fallbackMatch ?? "";
    }
}
