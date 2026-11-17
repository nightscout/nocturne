using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using FluentAssertions;
using Xunit;

namespace Nocturne.Aspire.Host.Tests;

public class AlertsNativeBuildTests
{
    private static readonly string SolutionRoot = Path.GetFullPath("solution-root");

    [Fact]
    public void With_cargo_the_api_waits_for_the_cargo_build_to_finish()
    {
        var builder = DistributedApplication.CreateBuilder();
        var api = builder.AddContainer("api", "api-image");

        AlertsNativeBuild.AddTo(builder, api, SolutionRoot, cargoAvailable: true);

        var cargo = builder.Resources.OfType<ExecutableResource>()
            .Should().ContainSingle(r => r.Name == AlertsNativeBuild.ResourceName).Subject;
        cargo.Command.Should().Be("cargo");
        cargo.WorkingDirectory.Should().Be(Path.Combine(SolutionRoot, "crates"));
        api.Resource.Annotations.OfType<WaitAnnotation>().Should().ContainSingle(w =>
            w.Resource == cargo && w.WaitType == WaitType.WaitForCompletion);
    }

    [Fact]
    public void Without_cargo_the_api_waits_for_nothing()
    {
        var builder = DistributedApplication.CreateBuilder();
        var api = builder.AddContainer("api", "api-image");

        AlertsNativeBuild.AddTo(builder, api, SolutionRoot, cargoAvailable: false);

        builder.Resources.Should().NotContain(r => r.Name == AlertsNativeBuild.ResourceName);
        api.Resource.Annotations.OfType<WaitAnnotation>().Should().BeEmpty();
    }

    [Fact]
    public void A_command_that_exits_zero_runs() =>
        AlertsNativeBuild.CommandRuns("dotnet", "--version").Should().BeTrue();

    [Fact]
    public void A_command_that_fails_does_not_run() =>
        AlertsNativeBuild.CommandRuns("dotnet", "--no-such-option-for-nocturne").Should().BeFalse();

    [Fact]
    public void A_command_not_on_path_does_not_run() =>
        AlertsNativeBuild.CommandRuns("nocturne-no-such-command", "").Should().BeFalse();
}
