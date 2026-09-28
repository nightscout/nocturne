using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services.WriteBack;

/// <summary>
/// The write-back serializer swaps only the id converter: a record with no id still says so, and an
/// id read back through the same options is the upstream key as sent, never re-coerced.
/// </summary>
[Trait("Category", "Unit")]
public class UpstreamIdentityJsonTests
{
    [Fact]
    public void A_record_without_an_id_is_written_with_a_null_id()
    {
        var json = JsonSerializer.SerializeToElement(new Entry { Id = null, Sgv = 110 }, UpstreamIdentityJson.Options);

        json.GetProperty("_id").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("identifier").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("dexcom_7f3c2a91")]
    [InlineData("0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f")]
    [InlineData("5f1a2b3c4d5e6f7a8b9c0d1e")]
    public void An_id_is_read_back_verbatim(string id)
    {
        var entry = JsonSerializer.Deserialize<Entry>($$"""{"_id":"{{id}}","sgv":110}""", UpstreamIdentityJson.Options);

        entry!.Id.Should().Be(id);
    }

    [Fact]
    public void A_null_id_is_read_back_as_null()
    {
        var status = JsonSerializer.Deserialize<DeviceStatus>("""{"_id":null,"device":"loop"}""", UpstreamIdentityJson.Options);

        status!.Id.Should().BeNull();
        status.Device.Should().Be("loop");
    }
}
