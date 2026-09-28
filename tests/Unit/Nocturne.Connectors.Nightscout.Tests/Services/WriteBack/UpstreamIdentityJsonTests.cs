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
        var json = JsonSerializer.SerializeToElement(new Treatment { Id = null, EventType = "Note" }, UpstreamIdentityJson.Options);

        json.GetProperty("_id").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("identifier").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void An_entry_without_an_ObjectId_is_written_with_no_id()
    {
        var json = JsonSerializer.SerializeToElement(new Entry { Id = null, Sgv = 110 }, UpstreamIdentityJson.Options);

        json.TryGetProperty("_id", out _).Should().BeFalse();
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

    /// <summary>
    /// A pulled document's <c>identifier</c>, where write-back put the record's own key, reaches the
    /// decomposers beside its <c>_id</c>, for every document kind the connector pulls.
    /// </summary>
    [Fact]
    public void A_pulled_identifier_is_read_beside_the_id()
    {
        var entry = JsonSerializer.Deserialize<Entry>(
            """{"_id":"66f0a1b2c3d4e5f6a7b8c9d0","identifier":"dexcom_7f3c2a91","sgv":110}""", UpstreamIdentityJson.ReadOptions);
        var treatment = JsonSerializer.Deserialize<Treatment>(
            """{"_id":"66f0a1b2c3d4e5f6a7b8c9d1","identifier":"syn-3a7c","eventType":"Note"}""", UpstreamIdentityJson.ReadOptions);
        var status = JsonSerializer.Deserialize<DeviceStatus>(
            """{"_id":"66f0a1b2c3d4e5f6a7b8c9d2","identifier":"loop_status_42","device":"loop"}""", UpstreamIdentityJson.ReadOptions);

        (entry!.Id, entry.UpstreamIdentifier).Should().Be(("66f0a1b2c3d4e5f6a7b8c9d0", "dexcom_7f3c2a91"));
        (treatment!.Id, treatment.UpstreamIdentifier).Should().Be(("66f0a1b2c3d4e5f6a7b8c9d1", "syn-3a7c"));
        (status!.Id, status.UpstreamIdentifier).Should().Be(("66f0a1b2c3d4e5f6a7b8c9d2", "loop_status_42"));
        (status.ExtensionData ?? []).Should().NotContainKey("identifier");
    }

    [Fact]
    public void A_pulled_document_without_an_identifier_reads_as_before()
    {
        var entry = JsonSerializer.Deserialize<Entry>("""{"_id":"66f0a1b2c3d4e5f6a7b8c9d0","SGV":"110"}""", UpstreamIdentityJson.ReadOptions);

        entry!.Id.Should().Be("66f0a1b2c3d4e5f6a7b8c9d0");
        entry.UpstreamIdentifier.Should().BeNull();
        entry.Sgv.Should().Be(110);
    }

    [Fact]
    public void A_status_is_written_with_its_own_key_as_its_identifier()
    {
        var json = JsonSerializer.SerializeToElement(new DeviceStatus { Id = "loop_status_42", Device = "loop" }, UpstreamIdentityJson.Options);

        json.GetProperty("_id").GetString().Should().Be(MongoObjectId.Coerce("loop_status_42"));
        json.GetProperty("identifier").GetString().Should().Be("loop_status_42");
    }
}

