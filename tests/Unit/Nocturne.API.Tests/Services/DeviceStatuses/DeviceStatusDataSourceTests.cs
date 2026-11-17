using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Configuration;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.DeviceStatuses;

/// <summary>
/// A device status's data source is for the write-back only: no response carries it and no upload sets it.
/// </summary>
[Trait("Category", "Unit")]
public class DeviceStatusDataSourceTests
{
    private static readonly DeviceStatus Stamped = new()
    {
        Id = "65f0a1b2c3d4e5f601234567",
        Device = "loop://iphone",
        DataSource = DataSources.NightscoutConnector,
    };

    [Fact]
    public void ALegacyResponseDoesNotCarryIt()
    {
        var json = JsonSerializer.Serialize(Stamped, NightscoutJsonOptions.Create());

        json.Should().NotContain("data_source").And.NotContain("dataSource").And.NotContain(DataSources.NightscoutConnector);
    }

    [Fact]
    public void ABroadcastDoesNotCarryIt()
    {
        var json = JsonSerializer.Serialize(Stamped);

        json.Should().NotContain("data_source").And.NotContain("DataSource").And.NotContain(DataSources.NightscoutConnector);
    }

    [Theory]
    [InlineData("data_source")]
    [InlineData("dataSource")]
    [InlineData("DataSource")]
    public void AnUploadCannotSetIt(string key)
    {
        var upload = JsonSerializer.Deserialize<DeviceStatus>(
            $$"""{"device":"loop://iphone","{{key}}":"{{DataSources.NightscoutConnector}}"}""")!;

        upload.DataSource.Should().BeNull();
    }
}
