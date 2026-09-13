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
                    if (string.IsNullOrWhiteSpace(settings.StreamSettings.DeckLinkDevice))
                    {
                        settings.StreamSettings.DeckLinkDevice = "DeckLink Duo (1)";
                    }
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
        else
        {
            var defaultNames = new[] { "Sahyadri Facebook", "Sahyadri YouTube", "Sahyadri YouTube News" };
            var defaultUrls = new[] { "rtmps://live-api-s.facebook.com:443/rtmp/", "rtmp://a.rtmp.youtube.com/live2", "rtmp://a.rtmp.youtube.com/live2" };
            var defaultIds = new[] { "fb", "yt_main", "yt_news" };

            for (int i = 0; i < 3; i++)
            {
                if (i < config.Destinations.Count)
                {
                    if (string.IsNullOrWhiteSpace(config.Destinations[i].Name))
                    {
                        config.Destinations[i].Name = defaultNames[i];
                    }
                }
                else
                {
                    config.Destinations.Add(new DestinationConfig
                    {
                        Id = defaultIds[i],
                        Name = defaultNames[i],
                        Enabled = false,
                        ServerUrl = defaultUrls[i],
                        StreamKey = ""
                    });
                }
            }
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
                DeckLinkDevice = "go1080p25.mp4",
                VideoStandardCode = "Hp25",
                VideoEncoder = VideoEncoderType.LibX264,
                VideoBitrateKbps = 5000,
                KeyframeIntervalSeconds = 2,
                OutputResolution = "1920x1080",
                AudioBitrateKbps = 192,
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
