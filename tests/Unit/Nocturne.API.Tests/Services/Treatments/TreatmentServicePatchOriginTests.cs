using System.Text.Json;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// A PATCH merges into the stored projection, which echoes the origin's evidence; a patch that changes
/// <c>basalOrigin</c> states the origin, so the echoed evidence must not mark it a default.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentServicePatchOriginTests
{
    private static TempBasal Stored() => new()
    {
        Id = Guid.CreateVersion7(),
        StartTimestamp = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
        EndTimestamp = new DateTime(2026, 8, 1, 12, 30, 0, DateTimeKind.Utc),
        Rate = 1.2,
        Origin = TempBasalOrigin.Algorithm,
        OriginEvidence = TempBasalOriginEvidence.Uploader,
        App = "Trio",
    };

    private static TempBasal Patched(string patch)
    {
        var treatment = JsonSerializer.Deserialize<Treatment>(JsonSerializer.Serialize(TempBasalToTreatmentMapper.ToTreatment(Stored())))!;
        using var body = JsonDocument.Parse(patch);
        TreatmentService.ApplyJsonPatch(treatment, body.RootElement);
        var written = TreatmentDecomposer.MapToTempBasal(treatment, correlationId: null);
        TreatmentDecomposer.SettleEchoedTempBasalOrigin(written, treatment, Stored());
        return written;
    }

    [Fact]
    public void A_patch_naming_the_origin_states_it()
    {
        Patched("""{"basalOrigin": "Manual"}""").Should().BeEquivalentTo(new
        {
            Origin = TempBasalOrigin.Manual, OriginEvidence = (TempBasalOriginEvidence?)TempBasalOriginEvidence.Reported,
        });
    }

    [Fact]
    public void A_patch_of_another_field_leaves_the_default_a_default()
    {
        Patched("""{"notes": "edited"}""").OriginEvidence.Should().Be(TempBasalOriginEvidence.Uploader);
    }

    [Fact]
    public void A_patch_naming_the_stored_origin_leaves_a_default_a_default()
    {
        Patched("""{"basalOrigin": "Algorithm"}""").Should().BeEquivalentTo(new
        {
            Origin = TempBasalOrigin.Algorithm, OriginEvidence = (TempBasalOriginEvidence?)TempBasalOriginEvidence.Uploader,
        });
    }
}
