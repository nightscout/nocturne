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

    /// <summary>
    /// Before this serializer, a treatment went out through the web defaults, whose converter wrote
    /// the coerced id as both <c>_id</c> and <c>identifier</c>. The copies upstream are stored under
    /// that identifier, so a created treatment must keep going out exactly so.
    /// </summary>
    [Theory]
    [InlineData("65a1b2c3d4e5f60718293a4b")]
    [InlineData("syn-3a7c0e9f1b2d4c6e")]
    [InlineData("4f1c1d2e-3a4b-4c5d-8e6f-7a8b9c0d1e2f")]
    [InlineData("0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f")]
    public void A_treatment_is_written_under_the_ids_earlier_write_backs_sent(string id)
    {
        var treatment = new Treatment { Id = id, EventType = "Correction Bolus", Insulin = 1 };

        var sent = JsonSerializer.SerializeToElement(treatment, UpstreamIdentityJson.Options);
        var before = JsonSerializer.SerializeToElement(treatment, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        sent.GetProperty("_id").GetString().Should().Be(before.GetProperty("_id").GetString()).And.Be(MongoObjectId.Coerce(id));
        sent.GetProperty("identifier").GetString().Should().Be(before.GetProperty("identifier").GetString());
    }

    [Fact]
    public void A_treatment_is_written_under_its_legacy_id_rather_than_the_uuid_it_is_served_by()
    {
        var json = JsonSerializer.SerializeToElement(
            new Treatment { Id = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f", LegacyId = "syn-3a7c0e9f1b2d4c6e", EventType = "Note" },
            UpstreamIdentityJson.Options);

        json.GetProperty("_id").GetString().Should().Be(MongoObjectId.Coerce("syn-3a7c0e9f1b2d4c6e"));
        json.GetProperty("identifier").GetString().Should().Be(MongoObjectId.Coerce("syn-3a7c0e9f1b2d4c6e"));
        json.TryGetProperty("legacyId", out _).Should().BeFalse();
    }

    [Fact]
    public void A_treatment_payload_carries_the_ids_it_is_given_and_leaves_out_a_null_one()
    {
        var treatment = new Treatment { Id = "65a1b2c3d4e5f60718293a4b", EventType = "Note", Notes = "kept" };

        var idOnly = UpstreamIdentityJson.TreatmentPayload(treatment, "66b000000000000000000001", null);
        var identifierOnly = UpstreamIdentityJson.TreatmentPayload(treatment, null, "syn-3a7c");

        ((string?)idOnly["_id"], idOnly.ContainsKey("identifier"), (string?)idOnly["notes"])
            .Should().Be(("66b000000000000000000001", false, "kept"));
        (identifierOnly.ContainsKey("_id"), (string?)identifierOnly["identifier"]).Should().Be((false, "syn-3a7c"));
    }

    private const string RecordUuid = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f";
    private static readonly string RecordPrefix = MongoObjectId.FromGuid(Guid.Parse(RecordUuid));

    /// <summary>
    /// Every identifier a released or merged write-back left a treatment's copy under: this release's
    /// coerced key, the record's own uuid prefix (v0.2.4 to v0.2.7 temp basals, main after #1960), and
    /// the raw key (v0.0.1 to v0.2.3), most current first and without repeats.
    /// </summary>
    [Theory]
    [InlineData("65a1b2c3d4e5f60718293a4b", new[] { "65a1b2c3d4e5f60718293a4b", "@prefix" })]
    [InlineData("syn-3a7c0e9f1b2d4c6e", new[] { "@coerced", "@prefix", "syn-3a7c0e9f1b2d4c6e" })]
    [InlineData("4F1C1D2E-3A4B-4C5D-8E6F-7A8B9C0D1E2F", new[] { "4f1c1d2e3a4b4c5d8e6f7a8b", "@prefix", "4F1C1D2E-3A4B-4C5D-8E6F-7A8B9C0D1E2F" })]
    [InlineData(null, new[] { "@prefix", RecordUuid })]
    public void A_treatment_is_looked_for_under_every_form_a_write_back_sent_it_under(string? legacyId, string[] expected)
    {
        var treatment = new Treatment { Id = legacyId ?? RecordPrefix, LegacyId = legacyId, RecordId = Guid.Parse(RecordUuid), EventType = "Temp Basal" };

        UpstreamIdentityJson.TreatmentWireForms(treatment).Should().Equal(expected.Select(f => f switch
        {
            "@prefix" => RecordPrefix,
            "@coerced" => MongoObjectId.Coerce(legacyId),
            _ => f,
        }));
    }

    [Fact]
    public void A_status_is_written_with_its_own_key_as_its_identifier()
    {
        var json = JsonSerializer.SerializeToElement(new DeviceStatus { Id = "loop_status_42", Device = "loop" }, UpstreamIdentityJson.Options);

        json.GetProperty("_id").GetString().Should().Be(MongoObjectId.Coerce("loop_status_42"));
        json.GetProperty("identifier").GetString().Should().Be("loop_status_42");
    }
}

