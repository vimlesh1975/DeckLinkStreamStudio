using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DeckLinkStreamStudio.Models;

namespace DeckLinkStreamStudio;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public bool DarkMode { get; set; } = true;
    public string DefaultRecordingDirectory { get; set; } = @"D:\StreamRecordings";
    public bool AutoReconnect { get; set; } = true;

    public StreamConfig StreamSettings { get; set; } = new();

    public static string SettingsFilePath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "DeckLinkStreamStudio");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir, "appsettings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsFilePath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null && settings.StreamSettings != null)
                {
                    EnsureDefaultDestinations(settings.StreamSettings);
                    return settings;
                }
            }
        }
        catch { }

        return CreateDefault();
    }

    private static void EnsureDefaultDestinations(StreamConfig config)
    {
        if (config.Destinations == null || config.Destinations.Count == 0)
        {
            config.Destinations = new List<DestinationConfig>
            {
                new() { Id = "fb", Name = "Sahyadri Facebook", Enabled = true, ServerUrl = "rtmps://live-api-s.facebook.com:443/rtmp/", StreamKey = "" },
                new() { Id = "yt_main", Name = "Sahyadri YouTube", Enabled = true, ServerUrl = "rtmp://a.rtmp.youtube.com/live2", StreamKey = "" },
                new() { Id = "yt_news", Name = "Sahyadri YouTube News", Enabled = true, ServerUrl = "rtmp://a.rtmp.youtube.com/live2", StreamKey = "" }
            };
        }
    }

    public void Save()
    {
        try
        {
            var path = SettingsFilePath;
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    public static AppSettings CreateDefault()
    {
        var settings = new AppSettings
        {
            StreamSettings = new StreamConfig
            {
                DeckLinkDevice = "DeckLink SDI 4K",
                VideoStandardCode = "Hi50",
                VideoEncoder = VideoEncoderType.H264_NVENC,
                VideoBitrateKbps = 6500,
                KeyframeIntervalSeconds = 2,
                OutputResolution = "Original",
                AudioBitrateKbps = 192,
                EnableLocalArchive = false,
                ArchiveDirectory = @"D:\StreamRecordings",
                Destinations = new List<DestinationConfig>
                {
                    new() { Id = "fb", Name = "Sahyadri Facebook", Enabled = true, ServerUrl = "rtmps://live-api-s.facebook.com:443/rtmp/", StreamKey = "" },
                    new() { Id = "yt_main", Name = "Sahyadri YouTube", Enabled = true, ServerUrl = "rtmp://a.rtmp.youtube.com/live2", StreamKey = "" },
                    new() { Id = "yt_news", Name = "Sahyadri YouTube News", Enabled = true, ServerUrl = "rtmp://a.rtmp.youtube.com/live2", StreamKey = "" }
                }
            }
        };

        return settings;
    }
}
