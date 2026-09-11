using System;
using System.Collections.Generic;

namespace DeckLinkStreamStudio.Models;

public sealed class DestinationConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string ServerUrl { get; set; } = "";
    public string StreamKey { get; set; } = "";
    public StreamingProtocol Protocol { get; set; } = StreamingProtocol.RTMP;

    public bool HasValidTarget => !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(StreamKey);

    public string FullUrl
    {
        get
        {
            var server = (ServerUrl ?? "").Trim().TrimEnd('/');
            var key = (StreamKey ?? "").Trim().TrimStart('/');
            if (string.IsNullOrWhiteSpace(key))
                return "";
            return $"{server}/{key}";
        }
    }
}

public sealed class StreamConfig
{
    // Input Source (File loop or DeckLink hardware)
    public string DeckLinkDevice { get; set; } = "go1080p25.mp4";
    public string VideoStandardCode { get; set; } = "Hp25";
    public string VideoInput { get; set; } = "sdi"; // sdi, hdmi, optical_sdi
    public string AudioInput { get; set; } = "embedded";
    public int AudioChannels { get; set; } = 2;
    public int AudioDelayMs { get; set; } = 0;
    public bool Deinterlace { get; set; } = true; // YADIF broadcast deinterlacing for 1080i50

    // 3 Streaming Destinations: Sahyadri Facebook, Sahyadri YouTube, Sahyadri YouTube News
    public List<DestinationConfig> Destinations { get; set; } = new()
    {
        new() { Id = "fb", Name = "Sahyadri Facebook", Enabled = true, ServerUrl = "rtmps://live-api-s.facebook.com:443/rtmp/", StreamKey = "" },
        new() { Id = "yt_main", Name = "Sahyadri YouTube", Enabled = true, ServerUrl = "rtmp://a.rtmp.youtube.com/live2", StreamKey = "" },
        new() { Id = "yt_news", Name = "Sahyadri YouTube News", Enabled = true, ServerUrl = "rtmp://a.rtmp.youtube.com/live2", StreamKey = "" }
    };

    // Video Encoding
    public VideoEncoderType VideoEncoder { get; set; } = VideoEncoderType.Auto;
    public int VideoBitrateKbps { get; set; } = 6500;
    public int KeyframeIntervalSeconds { get; set; } = 2;
    public string OutputResolution { get; set; } = "Original"; // Original, 1920x1080, 1280x720, 854x480
    public int TargetFps { get; set; } = 0; // 0 = source fps, 25, 30, 50, 60

    // Audio Encoding
    public int AudioBitrateKbps { get; set; } = 192;
    public int AudioSampleRate { get; set; } = 48000;

    // In-App Video Preview Dimensions
    public int PreviewCenterWidth { get; set; } = 444;
    public int PreviewTotalHeight { get; set; } = 250;
    public int PreviewMeterWidth { get; set; } = 18;
    public int PreviewFps { get; set; } = 15;
}

public sealed class StreamStats
{
    public bool IsActive { get; set; }
    public StreamStatus Status { get; set; } = StreamStatus.Offline;
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public double CurrentBitrateKbps { get; set; }
    public double SingleStreamBitrateKbps { get; set; }
    public int ActiveDestinations { get; set; } = 1;
    public double CurrentFps { get; set; }
    public long TotalFrames { get; set; }
    public long DroppedFrames { get; set; }
    public double SpeedRatio { get; set; } = 1.0;
    public double CpuPercent { get; set; }
    public string StatusText { get; set; } = "Offline";
    public string LastError { get; set; } = "";
}
