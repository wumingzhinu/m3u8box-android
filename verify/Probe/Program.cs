using N_m3u8DL_RE.Parser.Config;

namespace M3U8Box.Verify;

/// <summary>
/// Entry point for the feasibility probe.
///
/// Exists so the AOT compiler treats <see cref="Probe"/> as reachable and
/// therefore analyses the upstream assemblies it touches. The probe is never
/// meant to be run as an app; CI only needs the compiler to walk this code
/// path.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var config = new ParserConfig
        {
            Url = args.Length > 0 ? args[0] : "https://example.invalid/probe.m3u8",
            // Supplying both a key and an IV makes the AES-128 branch of the
            // key processor resolve entirely offline, so this stays side-effect
            // free even if someone does run it.
            CustomeKey = new byte[16],
            CustomeIV = new byte[16],
        };

        Probe.TouchParserSurface(config);
        Probe.SupportedEncryptionMethods();
        Probe.TouchKeyProcessor(config);

        return 0;
    }
}
