using Android.App;
using Android.OS;

namespace M3U8Box.Verify;

/// <summary>
/// Minimal launcher activity for the feasibility probe.
/// </summary>
/// <remarks>
/// The probe is a compile target, not a shipping app. Declaring
/// <c>AndroidApplication</c> in the project file is required to satisfy the
/// Android application targets, and those targets in turn require a launcher
/// activity before the build will produce an APK. Without this the build fails
/// with "The PrivateSdkAssemblies ItemGroup is required for
/// _ComputeAssembliesToCompileToNative", because only the application targets
/// populate that group.
/// </remarks>
[Activity(Label = "M3U8Box Probe", MainLauncher = true)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // CacheDir, not an arbitrary path: it always exists, is writable, and is
        // private to the app. Upstream resolves its own directories from
        // Environment.ProcessPath, which is null on Android, so every path the
        // engine touches has to be supplied explicitly.
        var workDir = System.IO.Path.Combine(CacheDir?.AbsolutePath ?? FilesDir?.AbsolutePath ?? ".", "probe");

        // Referenced so the linker keeps the upstream touch points alive
        // through the publish pass. A failure here is a genuine problem with the
        // upstream code on Android and should surface, not be swallowed.
        Probe.RunAll();
        Probe.TouchDownloadEngine(workDir);
    }
}
