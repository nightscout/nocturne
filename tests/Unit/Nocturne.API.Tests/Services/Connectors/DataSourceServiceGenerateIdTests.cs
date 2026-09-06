using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.API.Services.Connectors;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// The delete endpoint resolves a data-source id back to a device by re-listing the active sources,
/// so an id a client holds must still resolve after the API restarts.
/// </summary>
[Trait("Category", "Unit")]
public class DataSourceServiceGenerateIdTests
{
    private const string DeviceId = "xDrip-DexcomG6 Samsung Galaxy S21";

    [Fact]
    public void GenerateId_IsStableForTheSameDevice()
    {
        DataSourceService.GenerateId(DeviceId).Should().Be(DataSourceService.GenerateId(DeviceId));
    }

    [Theory]
    [InlineData(DeviceId)]
    [InlineData("dexcom-connector")]
    [InlineData("")]
    public void GenerateId_MatchesTheDataSourceIdFormat(string deviceId)
    {
        Regex.IsMatch(DataSourceService.GenerateId(deviceId), "^ds-[0-9a-f]{8}$").Should().BeTrue();
    }

    /// <summary>
    /// Pinned so a change of hash algorithm is a deliberate, visible break rather than a silent
    /// invalidation of every id a client is holding.
    /// </summary>
    [Theory]
    [InlineData(DeviceId, "ds-8ba3c03d")]
    [InlineData("dexcom-connector", "ds-983acc04")]
    public void GenerateId_DerivesFromTheDeviceIdentifier(string deviceId, string expected)
    {
        DataSourceService.GenerateId(deviceId).Should().Be(expected);
    }

    [Fact]
    public void GenerateId_DiffersBetweenDevices()
    {
        DataSourceService.GenerateId("dexcom-connector")
            .Should().NotBe(DataSourceService.GenerateId("nightscout-connector"));
    }
}
