using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Xunit;

namespace Nocturne.Alerts.Native.Tests;

public class NativeGateTests
{
    /// <summary>
    /// CI sets NOCTURNE_ALERTS_REQUIRE_NATIVE=1 after building the cdylib so a
    /// broken probe cannot silently skip this assembly's parity suite. The gate
    /// itself is the shared <see cref="NativeLibraryGate"/> in the corpus
    /// generator's Harness.
    /// </summary>
    [Fact]
    public void Native_library_is_present_when_required() =>
        NativeLibraryGate.AssertPresentWhenRequired();
}
