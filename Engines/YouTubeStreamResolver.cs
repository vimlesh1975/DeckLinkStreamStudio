using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DeckLinkStreamStudio.Engines;

public sealed class YouTubeResolutionResult
{
    public bool Success { get; set; }
    public string[] StreamUrls { get; set; } = Array.Empty<string>();
    public string VideoUrl => StreamUrls.Length > 0 ? StreamUrls[0] : "";
    public string AudioUrl => StreamUrls.Length > 1 ? StreamUrls[1] : (StreamUrls.Length > 0 ? StreamUrls[0] : "");
    public bool IsDualStream => StreamUrls.Length >= 2;
    public string ErrorMessage { get; set; } = "";
}

public static class YouTubeStreamResolver
{
    private static readonly ConcurrentDictionary<string, (DateTime Expiry, YouTubeResolutionResult Result)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    public static bool IsYouTubeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var u = url.Trim();
        return u.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
               u.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNetworkUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var u = url.Trim();
        return u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               u.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
               u.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
               u.StartsWith("srt://", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ResolveYtDlpPath()
    {
        // 1. App BaseDirectory
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var p1 = Path.Combine(exeDir, "yt-dlp.exe");
        if (File.Exists(p1)) return p1;

        // 2. Current Directory
        var p2 = Path.Combine(Directory.GetCurrentDirectory(), "yt-dlp.exe");
        if (File.Exists(p2)) return p2;

        // 3. Project Root (Development)
        var p3 = @"D:\_projects\streaming\yt-dlp.exe";
        if (File.Exists(p3)) return p3;

        // 4. Release output folder
        var p4 = @"D:\_projects\streaming\bin\Release\net10.0-windows\win-x64\yt-dlp.exe";
        if (File.Exists(p4)) return p4;

        // 5. Common system paths
        var fallbacks = new[]
        {
            @"D:\software\net10.0-windows\net10.0-windows\yt-dlp.exe",
            @"D:\_projects\test\bin\apps\yt-dlp.exe",
            @"D:\net10.0-windows\net10.0-windows\yt-dlp.exe"
        };
        foreach (var fb in fallbacks)
        {
            if (File.Exists(fb)) return fb;
        }

        return "yt-dlp.exe";
    }

    public static string? ResolveNodePath()
    {
        var candidates = new[]
        {
            @"C:\Program Files\nodejs\node.exe",
            @"C:\Program Files (x86)\nodejs\node.exe"
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    public static YouTubeResolutionResult ResolveStreamUrls(string url, Action<string>? logCallback = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new YouTubeResolutionResult { Success = false, ErrorMessage = "URL is empty" };
        }

        var cleanUrl = url.Trim();

        // Check in-memory cache
        if (_cache.TryGetValue(cleanUrl, out var cached) && DateTime.UtcNow < cached.Expiry)
        {
            logCallback?.Invoke($"[YOUTUBE] Using cached stream URLs for {GetDisplayUrl(cleanUrl)}");
            return cached.Result;
        }

        var ytDlpPath = ResolveYtDlpPath();
        if (string.IsNullOrWhiteSpace(ytDlpPath))
        {
            var err = "yt-dlp.exe not found on system.";
            logCallback?.Invoke($"[YOUTUBE ERROR] {err}");
            return new YouTubeResolutionResult { Success = false, ErrorMessage = err };
        }

        logCallback?.Invoke($"[YOUTUBE] Resolving stream via yt-dlp: {GetDisplayUrl(cleanUrl)}");

        try
        {
            var nodePath = ResolveNodePath();
            var jsArg = !string.IsNullOrWhiteSpace(nodePath) ? $"--js-runtimes \"node:{nodePath}\" " : "";
            // Query manifest_url (master multi-track HLS variant) and -g as fallback
            var args = $"{jsArg}--no-playlist --print \"manifest_url\" -g \"{cleanUrl}\"";

            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return new YouTubeResolutionResult { Success = false, ErrorMessage = "Failed to start yt-dlp process" };
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);

            var urls = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(s => s.Trim())
                             .Where(s => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                         s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                             .ToArray();

            // 1. Look for master HLS variant playlist (contains /hls_variant/ or /file/index.m3u8)
            var masterHls = urls.FirstOrDefault(u => u.Contains("/hls_variant/") || u.EndsWith("/file/index.m3u8", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(masterHls))
            {
                var result = new YouTubeResolutionResult
                {
                    Success = true,
                    StreamUrls = new[] { masterHls }
                };
                _cache[cleanUrl] = (DateTime.UtcNow.Add(CacheDuration), result);
                logCallback?.Invoke($"[YOUTUBE] Master Live HLS Stream resolved: {GetDisplayUrl(masterHls)}");
                return result;
            }

            // 2. Fallback to direct stream URLs
            if (urls.Length > 0)
            {
                var result = new YouTubeResolutionResult
                {
                    Success = true,
                    StreamUrls = urls
                };
                _cache[cleanUrl] = (DateTime.UtcNow.Add(CacheDuration), result);
                logCallback?.Invoke($"[YOUTUBE] Resolved {urls.Length} stream(s) (Dual-track: {result.IsDualStream})");
                return result;
            }

            var errorMsg = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : "No stream URLs returned by yt-dlp.";
            var firstErrLine = errorMsg.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                       .FirstOrDefault(l => l.Contains("ERROR:", StringComparison.OrdinalIgnoreCase)) ?? errorMsg;
            logCallback?.Invoke($"[YOUTUBE ERROR] {firstErrLine}");
            return new YouTubeResolutionResult { Success = false, ErrorMessage = firstErrLine };
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"[YOUTUBE EXCEPTION] {ex.Message}");
            return new YouTubeResolutionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public static string GetDisplayUrl(string url, int maxLength = 45)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var u = url.Trim();
        if (u.Length <= maxLength) return u;
        return u.Substring(0, maxLength - 3) + "...";
    }

    public static void InvalidateCache(string? url = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            _cache.Clear();
        else
            _cache.TryRemove(url.Trim(), out _);
    }
}
