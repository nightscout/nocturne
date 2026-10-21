using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A treatment that omits every optional field still fills every NOT NULL string column of the
/// records it maps to. This is why a 23502 (a required value missing) is answered as a Nocturne
/// fault that stops the batch, not as the client's data: no field a client may leave out reaches
/// a required column empty.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentRequiredColumnsTests
{
    private static readonly Microsoft.EntityFrameworkCore.Metadata.IModel Model =
        new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql("Host=unused").Options).Model;

    private static Treatment Bare(string eventType) => new() { EventType = eventType };

    public static TheoryData<string, object> MappedEntities() => new()
    {
        { "bolus", BolusMapper.ToEntity(TreatmentDecomposer.MapToBolus(Bare("Correction Bolus"), null)) },
        { "carb intake", CarbIntakeMapper.ToEntity(TreatmentDecomposer.MapToCarbIntake(Bare("Carb Correction"), null)) },
        { "bg check", BGCheckMapper.ToEntity(TreatmentDecomposer.MapToBGCheck(Bare("BG Check"), null)) },
        { "note", NoteMapper.ToEntity(TreatmentDecomposer.MapToNote(Bare("Note"), null, isAnnouncement: false)) },
        { "announcement", NoteMapper.ToEntity(TreatmentDecomposer.MapToNote(Bare("Announcement"), null, isAnnouncement: true)) },
        { "device event", DeviceEventMapper.ToEntity(TreatmentDecomposer.MapToDeviceEvent(Bare("Sensor Change"), null, DeviceEventType.SensorChange)) },
        { "bolus calculation", BolusCalculationMapper.ToEntity(TreatmentDecomposer.MapToBolusCalculation(Bare("Bolus Wizard"), null)) },
        { "temp basal", TempBasalMapper.ToEntity(TreatmentDecomposer.MapToTempBasal(Bare("Temp Basal"), null)) },
    };

    [Theory]
    [MemberData(nameof(MappedEntities))]
    public void A_bare_treatment_fills_every_required_string_column(string record, object entity)
    {
        var entityType = Model.FindEntityType(entity.GetType())!;
        var empty = entityType.GetProperties()
            .Where(p => !p.IsNullable && p.ClrType == typeof(string) && p.PropertyInfo is not null)
            .Where(p => p.PropertyInfo!.GetValue(entity) is null)
            .Select(p => p.Name)
            .ToList();

        empty.Should().BeEmpty($"a {record} mapped from a treatment with no optional fields must fill its required columns");
    }
}
