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
/// activity before ILCompiler will run. Without this the build fails with
/// "The PrivateSdkAssemblies ItemGroup is required for
/// _ComputeAssembliesToCompileToNative", because only the application targets
/// populate that group.
/// </remarks>
[Activity(Label = "M3U8Box Probe", MainLauncher = true)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Referenced so the linker keeps the upstream touch points alive
        // through the AOT pass. Any failure here is a real problem with the
        // upstream code on Android and should surface, not be swallowed.
        Probe.RunAll();
    }
}
