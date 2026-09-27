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

    private const string RecordUuid = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f";

    [Theory]
    [InlineData("5f1a2b3c4d5e6f7a8b9c0d1e")]
    [InlineData("syn-3a7c0e9f1b2d4c6e8a0b2d4f6a8c0e2b4d6f8a0c2e4a6c8e0a2c4e6a8c0e")]
    [InlineData("4f1c1d2e-3a4b-4c5d-8e6f-7a8b9c0d1e2f")]
    public void A_treatment_is_written_under_its_legacy_id_verbatim(string legacyId)
    {
        var json = JsonSerializer.SerializeToElement(
            new Treatment { Id = RecordUuid, LegacyId = legacyId, EventType = "Note" }, UpstreamIdentityJson.Options);

        json.GetProperty("_id").GetString().Should().Be(legacyId);
        json.GetProperty("identifier").GetString().Should().Be(legacyId);
        json.TryGetProperty("legacyId", out _).Should().BeFalse();
    }

    [Fact]
    public void A_treatment_without_a_legacy_id_is_written_under_its_record_id_prefix()
    {
        var json = JsonSerializer.SerializeToElement(
            new Treatment { Id = RecordUuid, EventType = "Note" }, UpstreamIdentityJson.Options);

        json.GetProperty("_id").GetString().Should().Be(MongoObjectId.FromGuid(Guid.Parse(RecordUuid)));
    }
}
