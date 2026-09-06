using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Services;
using Xunit;

namespace Nocturne.Core.Models.Tests.Services;

/// <summary>
/// <see cref="DataSourceInfo.DeviceIdHandle"/> is what tells a consumer whether the entry's
/// <see cref="DataSourceInfo.DeviceId"/> is a data source or a reported device string, so it has to
/// reach the wire — and as the named handle, not its ordinal.
/// </summary>
[Trait("Category", "Unit")]
public class DataSourceInfoSerializationTests
{
    [Theory]
    [InlineData(SourceHandle.Device, "device")]
    [InlineData(SourceHandle.DataSource, "dataSource")]
    [InlineData(SourceHandle.Unknown, "unknown")]
    public void DeviceIdHandle_SerializesAsItsNamedHandle(SourceHandle handle, string expected)
    {
        var json = JsonSerializer.Serialize(new DataSourceInfo { DeviceId = "openaps://rig", DeviceIdHandle = handle });

        using var parsed = JsonDocument.Parse(json);
        parsed.RootElement.GetProperty("deviceIdHandle").GetString().Should().Be(expected);
    }
}
