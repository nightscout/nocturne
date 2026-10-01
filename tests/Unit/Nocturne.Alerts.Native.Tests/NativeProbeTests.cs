using FluentAssertions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.Alerts.Native.Tests;

public class NativeProbeTests
{
    [Fact]
    public void The_expected_version_is_stamped_from_the_crate()
    {
        AlertsInterop.ExpectedVersion.Should().MatchRegex(@"^\d+\.\d+\.\d+");
    }

    [Fact]
    public void A_matching_version_with_every_export_passes()
    {
        AlertsInterop.Verify("1.2.3", "1.2.3", _ => true).IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void A_version_mismatch_fails()
    {
        var result = AlertsInterop.Verify("1.2.2", "1.2.3", _ => true);

        result.IsAvailable.Should().BeFalse();
        result.Failure.Should().Contain("'1.2.2'").And.Contain("'1.2.3'");
    }

    [Fact]
    public void A_missing_export_fails_and_is_named()
    {
        var result = AlertsInterop.Verify("1.2.3", "1.2.3", e => e != "nocturne_alerts_validate");

        result.IsAvailable.Should().BeFalse();
        result.Failure.Should().Contain("nocturne_alerts_validate");
    }

    [Fact]
    public void A_build_without_an_expected_version_fails()
    {
        AlertsInterop.Verify("1.2.3", "", _ => true).IsAvailable.Should().BeFalse();
    }

    [NativeFact]
    public void The_built_library_passes_the_probe_and_reports_its_releases()
    {
        var probe = AlertsInterop.Probe();

        probe.Failure.Should().BeNull();
        probe.Version.Should().Be(AlertsInterop.ExpectedVersion);
        probe.TzdbVersion.Should().MatchRegex(@"^\d{4}[a-z]$");
    }
}
