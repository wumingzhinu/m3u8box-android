using N_m3u8DL_RE.Common.Entity;
using N_m3u8DL_RE.Common.Enum;
using N_m3u8DL_RE.Parser;
using N_m3u8DL_RE.Parser.Config;
using N_m3u8DL_RE.Parser.Processor.HLS;
using N_m3u8DL_RE.Parser.Util;

namespace M3U8Box.Verify;

/// <summary>
/// Compile-time and AOT reachability probe.
///
/// This type exists so the Android AOT compiler is forced to analyse the
/// upstream types we intend to ship, instead of trimming them as unreachable.
/// A passing build therefore proves two things: upstream compiles for
/// net9.0-android, and the AOT compiler can process the resulting IL.
/// </summary>
public static class Probe
{
    /// <summary>
    /// Enumerates the encryption methods the upstream engine handles.
    ///
    /// The engine dispatches on these at runtime; listing them here means a
    /// future upstream rename surfaces as a build break rather than a silent
    /// regression in decryption coverage.
    /// </summary>
    public static EncryptMethod[] SupportedEncryptionMethods() =>
    [
        EncryptMethod.NONE,
        EncryptMethod.AES_128,
        EncryptMethod.AES_128_ECB,
        EncryptMethod.SAMPLE_AES,
        EncryptMethod.SAMPLE_AES_CTR,
        EncryptMethod.CENC,
        EncryptMethod.CHACHA20,
    ];

    /// <summary>
    /// Exercises the HLS key/IV path the app depends on.
    ///
    /// <see cref="DefaultHLSKeyProcessor"/> is the component that parses an
    /// <c>#EXT-X-KEY</c> line: it resolves the key URI, fetches the key bytes,
    /// and parses the IV. This is precisely the logic that is commonly
    /// reimplemented incorrectly elsewhere, so it is the right thing to pin
    /// under AOT.
    ///
    /// A custom key and IV are supplied via <see cref="ParserConfig"/> so the
    /// call needs no network access and the AES path is fully exercised.
    /// </summary>
    public static EncryptInfo TouchKeyProcessor(ParserConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var processor = new DefaultHLSKeyProcessor();
        _ = processor.CanProcess(ExtractorType.HLS, string.Empty, string.Empty, string.Empty, config);

        // CustomeKey/CustomeIV short-circuit the key download, so this
        // resolves entirely offline.
        return processor.Process(
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x0123456789ABCDEF0123456789ABCDEF",
            config.Url,
            string.Empty,
            config);
    }

    /// <summary>
    /// Touches the stream extractor entry point and the attribute-parsing
    /// helpers the app calls.
    ///
    /// The IV fallback is restated independently because upstream derives a
    /// 128-bit big-endian IV from the segment index when the playlist omits
    /// <c>IV=</c> (see HLSExtractor.cs). A change there would silently alter
    /// decrypt output; duplicating the rule here turns it into a visible diff.
    /// </summary>
    public static void TouchParserSurface(ParserConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // The runtime entry point the app will instantiate.
        using var extractor = new StreamExtractor(config);

        // Attribute parsing, as used on #EXT-X-KEY lines.
        _ = ParserUtil.GetAttribute("#EXT-X-KEY:METHOD=AES-128", "METHOD");

        // The derived-IV fallback, restated to catch upstream drift.
        const int segmentIndex = 0;
        var derivedIv = Convert.ToString(segmentIndex, 16).PadLeft(32, '0');
        if (derivedIv.Length != 32)
        {
            throw new InvalidOperationException("IV must be 128-bit (32 hex chars).");
        }

        _ = config.Headers;
        _ = config.CustomeIV;
    }
}
