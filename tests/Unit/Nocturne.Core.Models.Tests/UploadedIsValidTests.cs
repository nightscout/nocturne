using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary>
/// A treatment uploaded with <c>isValid: false</c> is stored deleted, so the span and temp basal
/// upload fields keep only <c>isValid: true</c>.
/// </summary>
[Trait("Category", "Unit")]
public class UploadedIsValidTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, null)]
    [InlineData(null, null)]
    public void A_span_keeps_isValid_only_when_true(bool? uploaded, bool? kept)
    {
        var metadata = new Dictionary<string, object>();
        SpanUploadedFields.Keep(metadata, new Treatment { IsValid = uploaded });

        var served = new Treatment();
        SpanUploadedFields.Restore(served, metadata);

        served.IsValid.Should().Be(kept);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, null)]
    [InlineData(null, null)]
    public void A_temp_basal_keeps_isValid_only_when_true(bool? uploaded, bool? kept)
    {
        var record = TempBasalUploadedFields.Keep(null, new Treatment { IsValid = uploaded });

        var served = new Treatment();
        TempBasalUploadedFields.Restore(served, new TempBasal { AdditionalProperties = record });

        served.IsValid.Should().Be(kept);
    }

    [Theory]
    [InlineData("OMNIPOD_DASH", null, "7", 7L)]
    [InlineData(null, "serial", "7", 7L)]
    [InlineData(null, null, "7", null)]
    [InlineData("OMNIPOD_DASH", null, "7,8", null)]
    [InlineData("OMNIPOD_DASH", null, null, null)]
    public void A_pump_id_is_served_only_for_a_record_whose_uploader_described_its_pump(
        string? pumpType, string? pumpSerial, string? pumpRecordId, long? served)
    {
        var record = UploadedPumpFields.Keep(null, new Treatment { PumpType = pumpType, PumpSerial = pumpSerial });

        var treatment = new Treatment();
        UploadedPumpFields.Restore(treatment, record, pumpRecordId);

        treatment.PumpId.Should().Be(served);
    }
}
