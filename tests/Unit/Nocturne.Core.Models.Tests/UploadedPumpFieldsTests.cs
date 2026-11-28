using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary><see cref="UploadedPumpFields"/> over the metadata of a state span or profile activation.</summary>
[Trait("Category", "Unit")]
public class UploadedPumpFieldsTests
{
    private static Dictionary<string, object> Kept(Treatment treatment)
    {
        var metadata = new Dictionary<string, object>();
        UploadedPumpFields.KeepInMetadata(metadata, treatment);
        return metadata;
    }

    private static Dictionary<string, object> AsStored(Dictionary<string, object> metadata) =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(metadata))!;

    private static Treatment Served(IDictionary<string, object> metadata)
    {
        var treatment = new Treatment();
        UploadedPumpFields.RestoreFromMetadata(treatment, metadata);
        return treatment;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_pump_field_is_served_as_uploaded_in_memory_or_read_back(bool readBack)
    {
        var metadata = Kept(new Treatment
        {
            IsValid = true, PumpId = 4102, PumpType = "OMNIPOD_DASH", PumpSerial = "33013206",
        });

        var served = Served(readBack ? AsStored(metadata) : metadata);

        served.IsValid.Should().BeTrue();
        served.PumpId.Should().Be(4102);
        served.PumpType.Should().Be("OMNIPOD_DASH");
        served.PumpSerial.Should().Be("33013206");
    }

    [Fact]
    public void A_span_row_stored_before_the_rule_was_shared_serves_the_same_fields()
    {
        var stored = AsStored(new Dictionary<string, object>
        {
            ["isValid"] = true, ["pumpId"] = 4102L, ["pumpType"] = "OMNIPOD_DASH", ["pumpSerial"] = "33013206",
        });

        var served = Served(stored);

        served.IsValid.Should().BeTrue();
        served.PumpId.Should().Be(4102);
        served.PumpType.Should().Be("OMNIPOD_DASH");
        served.PumpSerial.Should().Be("33013206");
    }

    [Fact]
    public void A_span_keeps_no_type_and_no_empty_pump_type_or_serial()
    {
        var metadata = Kept(new Treatment { BolusType = "NORMAL", PumpType = "", PumpSerial = "" });

        metadata.Should().BeEmpty();
    }

    [Fact]
    public void A_pump_id_is_served_from_a_span_without_a_pump_type_or_serial()
    {
        var served = Served(AsStored(Kept(new Treatment { PumpId = 4102 })));

        served.PumpId.Should().Be(4102);
        served.PumpType.Should().BeNull();
    }

    [Fact]
    public void A_span_with_nothing_kept_serves_nothing()
    {
        var served = Served(new Dictionary<string, object>());

        served.IsValid.Should().BeNull();
        served.PumpId.Should().BeNull();
        served.PumpType.Should().BeNull();
        served.PumpSerial.Should().BeNull();
    }
}
