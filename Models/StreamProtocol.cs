using System;
using System.Collections.Generic;

namespace DeckLinkStreamStudio.Models;

public enum StreamingProtocol
{
    RTMP,
    SRT_Caller,
    SRT_Listener,
    UDP_MpegTs,
    Local_Record_Only
}

public enum VideoEncoderType
{
    H264_NVENC,
    HEVC_NVENC,
    LibX264
}

public enum StreamStatus
{
    Offline,
    StandbyPreview,
    Connecting,
    OnAir,
    Error
}

public sealed class DeckLinkStandardItem
{
    public string Code { get; set; } = "Hi50";
    public string DisplayName { get; set; } = "1080i50 (Hi50)";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double FrameRate { get; set; } = 25.0;
    public bool IsInterlaced { get; set; } = true;

    public override string ToString() => DisplayName;

    public static readonly IReadOnlyList<DeckLinkStandardItem> Standards = new List<DeckLinkStandardItem>
    {
        new() { Code = "Hi50", DisplayName = "1080i50 (Hi50) - Broadcast PAL", Width = 1920, Height = 1080, FrameRate = 25.0, IsInterlaced = true },
        new() { Code = "Hp50", DisplayName = "1080p50 (Hp50) - 50 FPS Progressive", Width = 1920, Height = 1080, FrameRate = 50.0, IsInterlaced = false },
        new() { Code = "Hp25", DisplayName = "1080p25 (Hp25) - 25 FPS Progressive", Width = 1920, Height = 1080, FrameRate = 25.0, IsInterlaced = false },
        new() { Code = "Hi59", DisplayName = "1080i59.94 (Hi59) - Broadcast NTSC", Width = 1920, Height = 1080, FrameRate = 29.97, IsInterlaced = true },
        new() { Code = "Hp60", DisplayName = "1080p60 (Hp60) - 60 FPS Progressive", Width = 1920, Height = 1080, FrameRate = 60.0, IsInterlaced = false },
        new() { Code = "Hp30", DisplayName = "1080p30 (Hp30) - 30 FPS Progressive", Width = 1920, Height = 1080, FrameRate = 30.0, IsInterlaced = false },
        new() { Code = "hp50", DisplayName = "720p50 (hp50) - 50 FPS HD", Width = 1280, Height = 720, FrameRate = 50.0, IsInterlaced = false },
        new() { Code = "hp60", DisplayName = "720p60 (hp60) - 60 FPS HD", Width = 1280, Height = 720, FrameRate = 60.0, IsInterlaced = false },
        new() { Code = "pal ", DisplayName = "PAL 576i (pal ) - Standard Def", Width = 720, Height = 576, FrameRate = 25.0, IsInterlaced = true },
        new() { Code = "ntsc", DisplayName = "NTSC 480i (ntsc) - Standard Def", Width = 720, Height = 480, FrameRate = 29.97, IsInterlaced = true },
        new() { Code = "auto", DisplayName = "Auto Detect Video Mode", Width = 1920, Height = 1080, FrameRate = 25.0, IsInterlaced = false }
    };
}

public sealed class PlatformPreset
{
    public string Name { get; set; } = "YouTube Live";
    public StreamingProtocol Protocol { get; set; } = StreamingProtocol.RTMP;
    public string DefaultServerUrl { get; set; } = "rtmp://a.rtmp.youtube.com/live2";
    public int RecommendedBitrateKbps { get; set; } = 6000;
    public int KeyframeIntervalSeconds { get; set; } = 2;

    public override string ToString() => Name;

    public static readonly IReadOnlyList<PlatformPreset> Presets = new List<PlatformPreset>
    {
        new() { Name = "YouTube Live (RTMP)", Protocol = StreamingProtocol.RTMP, DefaultServerUrl = "rtmp://a.rtmp.youtube.com/live2", RecommendedBitrateKbps = 6500, KeyframeIntervalSeconds = 2 },
        new() { Name = "Facebook Live (RTMPS)", Protocol = StreamingProtocol.RTMP, DefaultServerUrl = "rtmps://live-api-s.facebook.com:443/rtmp/", RecommendedBitrateKbps = 4500, KeyframeIntervalSeconds = 2 },
        new() { Name = "Twitch (RTMP)", Protocol = StreamingProtocol.RTMP, DefaultServerUrl = "rtmp://live.twitch.tv/app/", RecommendedBitrateKbps = 6000, KeyframeIntervalSeconds = 2 },
        new() { Name = "Custom RTMP / RTMPS Server", Protocol = StreamingProtocol.RTMP, DefaultServerUrl = "rtmp://127.0.0.1/live", RecommendedBitrateKbps = 5000, KeyframeIntervalSeconds = 2 },
        new() { Name = "SRT Caller (Push to Remote)", Protocol = StreamingProtocol.SRT_Caller, DefaultServerUrl = "127.0.0.1", RecommendedBitrateKbps = 6000, KeyframeIntervalSeconds = 1 },
        new() { Name = "SRT Listener (Broadcast Server)", Protocol = StreamingProtocol.SRT_Listener, DefaultServerUrl = "0.0.0.0", RecommendedBitrateKbps = 6000, KeyframeIntervalSeconds = 1 },
        new() { Name = "UDP / RTP Multicast (LAN)", Protocol = StreamingProtocol.UDP_MpegTs, DefaultServerUrl = "udp://239.255.0.1:1234?pkt_size=1316", RecommendedBitrateKbps = 6000, KeyframeIntervalSeconds = 1 },
        new() { Name = "Local Archive Recording Only", Protocol = StreamingProtocol.Local_Record_Only, DefaultServerUrl = "", RecommendedBitrateKbps = 8000, KeyframeIntervalSeconds = 1 }
    };
}
