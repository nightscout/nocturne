using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Platform;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform;

/// <summary>
/// Team writes open staff writes to a demo subject in a team, so an instance that never names the setting has to keep
/// them closed.
/// </summary>
public class DemoModeServiceTests
{
    [Fact]
    public void TeamWritesAreClosedWhenUnset() =>
        Service([]).AllowsTeamWrites.Should().BeFalse();

    [Fact]
    public void TeamWritesOpenWhenSet() =>
        Service(new() { ["DemoMode:AllowTeamWrites"] = "true" }).AllowsTeamWrites.Should().BeTrue();

    private static DemoModeService Service(Dictionary<string, string?> settings) =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<DemoModeService>.Instance);
}
