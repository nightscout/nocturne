using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.Alerts.ParityCorpus.Generator.Harness;

/// <summary>
/// Availability gate for the nocturne_alerts native library, shared by every test
/// project that exercises the FFI. The library is a cargo build artifact, not a
/// committed binary, so the regular per-project unit-test loop must be able to run
/// without it: tests decorated with <see cref="NativeFactAttribute"/> /
/// <see cref="NativeTheoryAttribute"/> are skipped with a clear message when it cannot
/// be loaded.
/// </summary>
/// <remarks>
/// Resolution order (see AlertsInterop.ResolveLibrary): the
/// <c>NOCTURNE_ALERTS_NATIVE_DIR</c> environment variable, the
/// <c>runtimes/{rid}/native</c> output layout packed by
/// Nocturne.Core.Alerts.Native.csproj when the cdylib existed at build time,
/// then the default loader search. Build the library with
/// <c>cargo build --release -p nocturne-alerts-ffi</c> from <c>crates/</c>.
/// Set <c>NOCTURNE_ALERTS_REQUIRE_NATIVE=1</c> (as the alerts-ffi-parity CI job does)
/// to turn silent skips into a hard failure via <see cref="AssertPresentWhenRequired"/>.
/// </remarks>
public static class NativeLibraryGate
{
    public const string SkipReason =
        "nocturne_alerts native library not found - build it with 'cargo build --release -p nocturne-alerts-ffi' " +
        "from crates/ (then rebuild this test project) or point NOCTURNE_ALERTS_NATIVE_DIR at the directory containing it.";

    public static readonly bool IsAvailable = Probe();

    /// <summary>
    /// Body of the per-assembly REQUIRE_NATIVE contract test. Each test assembly whose
    /// CI job builds the cdylib keeps its own <c>[Fact]</c> calling this, so a broken
    /// probe cannot silently skip that assembly's native suite.
    /// </summary>
    public static void AssertPresentWhenRequired()
    {
        if (Environment.GetEnvironmentVariable("NOCTURNE_ALERTS_REQUIRE_NATIVE") == "1" && !IsAvailable)
        {
            Assert.Fail(
                "NOCTURNE_ALERTS_REQUIRE_NATIVE=1 but the nocturne_alerts native library could not be loaded. " +
                SkipReason);
        }
    }

    private static bool Probe()
    {
        try
        {
            return AlertsInterop.IsAvailable();
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>A fact that is skipped when the native library is unavailable.</summary>
public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute()
    {
        if (!NativeLibraryGate.IsAvailable)
            Skip = NativeLibraryGate.SkipReason;
    }
}

/// <summary>A theory that is skipped when the native library is unavailable.</summary>
public sealed class NativeTheoryAttribute : TheoryAttribute
{
    public NativeTheoryAttribute()
    {
        if (!NativeLibraryGate.IsAvailable)
            Skip = NativeLibraryGate.SkipReason;
    }
}
