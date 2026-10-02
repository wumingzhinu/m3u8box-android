using N_m3u8DL_RE.Common.Entity;
using N_m3u8DL_RE.Common.Enum;
using N_m3u8DL_RE.Common.Util;
using N_m3u8DL_RE.CommandLine;
using N_m3u8DL_RE.Config;
using N_m3u8DL_RE.DownloadManager;
using N_m3u8DL_RE.Parser;
using N_m3u8DL_RE.Parser.Config;
using N_m3u8DL_RE.Util;

namespace M3U8Box.App.Core;

/// <summary>One selectable track in a master playlist.</summary>
/// <param name="Index">Position in the engine's stream list.</param>
/// <param name="Label">Human-readable summary shown in the picker.</param>
/// <param name="Bandwidth">Bits per second, 0 when unspecified.</param>
/// <param name="Resolution">"WxH" when the manifest declares one.</param>
/// <param name="Codecs">Declared codecs, may be empty.</param>
/// <param name="MediaType">Video, audio, or subtitles.</param>
/// <param name="SegmentCount">Number of segments, used to skip empty tracks.</param>
public sealed record TrackInfo(
    int Index,
    string Label,
    long Bandwidth,
    string Resolution,
    string Codecs,
    string MediaType,
    int SegmentCount)
{
    /// <summary>True when this is a video or an audio-only rendition.</summary>
    public bool IsVideo => MediaType is "VIDEO" or "";
}

/// <summary>Result of inspecting a URL without downloading anything.</summary>
public sealed record InspectResult(
    string Title,
    IReadOnlyList<TrackInfo> Tracks,
    bool IsLive,
    string ExtractorType)
{
    public IEnumerable<TrackInfo> VideoTracks => Tracks.Where(t => t.IsVideo);
    public IEnumerable<TrackInfo> AudioTracks => Tracks.Where(t => t.MediaType == "AUDIO");
}

/// <summary>Raised when a download fails, carrying a message fit for the UI.</summary>
public sealed class DownloadException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Drives the upstream engine: inspect a URL, then download a chosen track.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors the flow of the engine's own <c>Program.DoWorkAsync</c>, which is
/// deleted by the upstream rewrite because it is a private static wired to
/// argv. The sequence matters and is not arbitrary:
/// load source, extract streams, apply filters, fetch playlists, then hand the
/// selection to <see cref="SimpleDownloadManager"/>. Skipping the playlist fetch
/// leaves <c>StreamSpec.Playlist</c> null and the manager has nothing to
/// download.
/// </para>
/// <para>
/// Paths are supplied explicitly everywhere. The engine defaults several
/// directories from <c>Environment.ProcessPath</c>, which is null on Android.
/// </para>
/// </remarks>
public sealed class DownloadEngine
{
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36";

    private readonly string _workDir;
    private readonly string _outputDir;

    public DownloadEngine(string workDir, string outputDir)
    {
        _workDir = workDir;
        _outputDir = outputDir;
    }

    /// <summary>
    /// Build the parser configuration for a URL and any extra request headers.
    /// </summary>
    /// <remarks>
    /// Headers matter more than anything else here. Most CDNs reject a playlist
    /// request without the Referer and cookies the browser used, which is the
    /// usual reason a URL that plays in a browser fails to download.
    /// </remarks>
    public static ParserConfig CreateParserConfig(string url, IReadOnlyDictionary<string, string>? headers = null)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["user-agent"] = DefaultUserAgent,
        };

        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    merged[k.Trim()] = v.Trim();
                }
            }
        }

        return new ParserConfig
        {
            Url = url,
            OriginalUrl = url,
            BaseUrl = url,
            Headers = merged,

            // Cookies arrive as a single Cookie header alongside the others.
            // HTTPUtil.ConfigureCookies is for cookie *files*; a header is the
            // form the UI collects and the one these CDNs expect.
        };
    }

    /// <summary>
    /// Fetch and parse a URL, returning the selectable tracks.
    /// </summary>
    /// <param name="onLog">Receives human-readable progress and diagnostics.</param>
    public async Task<InspectResult> InspectAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        Action<string>? onLog = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new DownloadException("请输入 m3u8 链接");
        }

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            throw new DownloadException("只支持 http/https 链接");
        }

        var config = CreateParserConfig(url, headers);
        using var extractor = new StreamExtractor(config);

        onLog?.Invoke($"正在获取播放列表: {url}");

        await RetryUtil.WebRequestRetryAsync(async () =>
        {
            await extractor.LoadSourceFromUrlAsync(url);
            return true;
        });

        onLog?.Invoke($"清单类型: {extractor.ExtractorType}");

        var streams = await extractor.ExtractStreamsAsync();
        if (streams.Count == 0)
        {
            throw new DownloadException("未从该链接解析出任何媒体流");
        }

        // Ordering mirrors the engine: video first, then by descending
        // bandwidth, so the picker defaults to the best quality available.
        var ordered = streams
            .OrderBy(s => s.MediaType)
            .ThenByDescending(s => s.Bandwidth)
            .ToList();

        var tracks = ordered.Select((s, i) => Describe(i, s)).ToList();

        var isLive = ordered.Any(s => s.Playlist?.IsLive == true);

        return new InspectResult(
            Title: DeriveTitle(url),
            Tracks: tracks,
            IsLive: isLive,
            ExtractorType: extractor.ExtractorType.ToString());
    }

    /// <summary>
    /// Download one track, then report where the output landed.
    /// </summary>
    /// <param name="selection">Result of <see cref="InspectAsync"/>.</param>
    /// <param name="videoTrackIndex">Index into <c>selection.Tracks</c> of the video track, or -1 for audio only.</param>
    /// <param name="audioTrackIndex">Audio track to include, or null to take the best available per language.</param>
    public async Task<string> DownloadAsync(
        InspectResult selection,
        int videoTrackIndex,
        int? audioTrackIndex,
        string saveName,
        IReadOnlyDictionary<string, string>? headers = null,
        Action<string>? onLog = null)
    {
        if (videoTrackIndex < 0 && audioTrackIndex is null)
        {
            throw new DownloadException("请至少选择一个轨道");
        }

        // Rebuild against the real URL rather than carrying the inspect-phase
        // config across the call boundary, so a resume cannot silently reuse a
        // stale URL.
        var url = _lastInspectedUrl ?? throw new DownloadException("内部错误：缺少已解析的链接");
        var parserConfig = CreateParserConfig(url, headers);

        var safeName = SanitiseFileName(string.IsNullOrWhiteSpace(saveName) ? "video" : saveName);

        var option = new MyOption
        {
            Input = url,
            SaveName = safeName,
            SaveDir = _outputDir,
            TmpDir = Path.Combine(_workDir, safeName),

            // Android has no ffmpeg. BinaryMerge concatenates the already
            // decrypted segments, which is the only merge path that needs no
            // external binary. MuxAfterDone would shell out to ffmpeg.
            BinaryMerge = true,
            MuxAfterDone = false,
            SkipMerge = false,

            NoLog = true,
            WriteMetaJson = false,
            DelAfterDone = false,
            NoAnsiColor = true,
            ForceAnsiConsole = false,
        };

        var downloadConfig = new DownloaderConfig
        {
            MyOptions = option,
            DirPrefix = option.TmpDir!,
            Headers = parserConfig.Headers,
        };

        // Re-extract to obtain live StreamSpec instances. The InspectResult
        // deliberately carries only display metadata, because StreamSpec holds
        // mutable state that must not be shared across a resume.
        using var extractor = new StreamExtractor(parserConfig);
        await RetryUtil.WebRequestRetryAsync(async () =>
        {
            await extractor.LoadSourceFromUrlAsync(url);
            return true;
        });

        var streams = await extractor.ExtractStreamsAsync();
        var ordered = streams
            .OrderBy(s => s.MediaType)
            .ThenByDescending(s => s.Bandwidth)
            .ToList();

        var selected = new List<StreamSpec>();
        if (videoTrackIndex >= 0 && videoTrackIndex < ordered.Count)
        {
            selected.Add(ordered[videoTrackIndex]);
        }

        if (audioTrackIndex is int ai && ai >= 0 && ai < ordered.Count)
        {
            selected.Add(ordered[ai]);
        }
        else
        {
            // Take the highest-bitrate audio for each distinct language, which
            // is what the engine's AutoSelect does.
            foreach (var lang in ordered
                         .Where(s => s.MediaType == MediaType.AUDIO)
                         .Select(s => s.Language)
                         .Distinct())
            {
                var best = ordered
                    .Where(s => s.MediaType == MediaType.AUDIO && s.Language == lang)
                    .OrderByDescending(s => s.Bandwidth)
                    .First();
                selected.Add(best);
            }
        }

        selected = selected.Distinct().Where(s => s.SegmentsCount > 0).ToList();
        if (selected.Count == 0)
        {
            throw new DownloadException("选中的轨道没有可下载的分片");
        }

        // Playlists are not loaded during ExtractStreamsAsync for every format;
        // without this the manager receives specs with a null Playlist.
        if (selected.Any(s => s.Playlist is null))
        {
            await extractor.FetchPlayListAsync(selected);
        }

        onLog?.Invoke($"开始下载 {selected.Count} 条轨道，共 " +
                      $"{selected.Sum(s => s.SegmentsCount)} 个分片");

        Directory.CreateDirectory(_outputDir);

        var manager = new SimpleDownloadManager(downloadConfig, selected, extractor);
        var ok = await manager.StartDownloadAsync();

        if (!ok)
        {
            throw new DownloadException("下载失败，请查看日志");
        }

        // The engine writes into TmpDir and moves the merged result to
        // SaveDir; locate what actually landed.
        var produced = FindOutput(_outputDir, safeName);
        onLog?.Invoke(produced is null
            ? "下载完成，但未找到输出文件"
            : $"下载完成: {produced}");

        return produced ?? Path.Combine(_outputDir, safeName);
    }

    private string? _lastInspectedUrl;

    /// <summary>Record the URL that <see cref="InspectAsync"/> last parsed.</summary>
    public void RememberUrl(string url) => _lastInspectedUrl = url;

    private static string DeriveTitle(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return Uri.UnescapeDataString(name);
            }
        }
        catch
        {
            // Fall through to the host.
        }

        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return "video";
        }
    }

    private static TrackInfo Describe(int index, StreamSpec s)
    {
        var mediaType = s.MediaType?.ToString().ToUpperInvariant() ?? "";
        var resolution = s.Resolution ?? "";
        var codecs = s.Codecs ?? "";

        // Bandwidth is int? upstream, not long.
        var bandwidth = (long)(s.Bandwidth ?? 0);

        var height = resolution.Contains('x') ? resolution.Split('x').LastOrDefault() : "";

        var labelParts = new List<string> { $"#{index + 1}" };
        if (!string.IsNullOrEmpty(resolution))
        {
            labelParts.Add(resolution);
        }
        else if (!string.IsNullOrEmpty(height))
        {
            labelParts.Add($"{height}p");
        }

        if (bandwidth > 0)
        {
            labelParts.Add($"{bandwidth / 1000.0 / 1000.0:0.0} Mbps");
        }

        if (!string.IsNullOrEmpty(codecs))
        {
            labelParts.Add(codecs);
        }

        if (mediaType == "AUDIO")
        {
            labelParts.Add("音频");
            if (!string.IsNullOrEmpty(s.Language))
            {
                labelParts.Add(s.Language);
            }
        }
        else if (mediaType == "SUBTITLES")
        {
            labelParts.Add("字幕");
        }

        if (s.Playlist?.IsLive == true)
        {
            labelParts.Add("直播");
        }

        return new TrackInfo(
            Index: index,
            Label: string.Join(" | ", labelParts),
            Bandwidth: bandwidth,
            Resolution: resolution,
            Codecs: codecs,
            MediaType: mediaType,
            SegmentCount: s.SegmentsCount);
    }

    /// <summary>
    /// Strip characters Android and common filesystems reject, and cap the
    /// length. The engine sanitises names too, but doing it here keeps the file
    /// the user sees matching the name they typed.
    /// </summary>
    private static string SanitiseFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "video";
        }

        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    private static string? FindOutput(string outputDir, string baseName)
    {
        if (!Directory.Exists(outputDir))
        {
            return null;
        }

        var direct = Path.Combine(outputDir, baseName + ".mp4");
        if (File.Exists(direct))
        {
            return direct;
        }

        // The engine may append a resolution or part suffix, so match by prefix
        // and take the most recently written file.
        return Directory.EnumerateFiles(outputDir, baseName + "*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
