using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Xunit;
using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

[Trait("Category", "Unit")]
public class TreatmentDecomposerTempBasalDurationTests
{
    private const long Start = 1700000000000;

    private static string ServedDuration(Treatment treatment) =>
        JsonDocument.Parse(JsonSerializer.Serialize(treatment)).RootElement.GetProperty("duration").GetRawText();

    private static V4Models.TempBasal Persisted(V4Models.TempBasal tempBasal) =>
        TempBasalMapper.ToDomainModel(TempBasalMapper.ToEntity(tempBasal));

    private static Treatment Upload(double duration) =>
        TempBasalToTreatmentMapper.ToTreatment(
            TreatmentDecomposer.MapToTempBasal(
                new Treatment { EventType = "Temp Basal", Mills = Start, Duration = duration, Absolute = 0.5 },
                correlationId: null));

    [Fact]
    public void AnUploadedFractionalDurationIsServedAsUploaded()
    {
        ServedDuration(Upload(4.962561716636022)).Should().Be("4.962561716636022");
    }

    [Fact]
    public void AnUploadedFractionalDurationSurvivesAJsonUploadAndRead()
    {
        var posted = JsonSerializer.Deserialize<Treatment>(
            """{"eventType":"Temp Basal","mills":1700000000000,"duration":4.962561716636022,"absolute":0.5}""")!;
        var stored = TreatmentDecomposer.MapToTempBasal(posted, correlationId: null);

        ServedDuration(TempBasalToTreatmentMapper.ToTreatment(stored)).Should().Be("4.962561716636022");
    }

    [Fact]
    public void AnUploadedFractionalDurationSurvivesTheDatabase()
    {
        var stored = TreatmentDecomposer.MapToTempBasal(
            new Treatment { EventType = "Temp Basal", Mills = Start, Duration = 4.962561716636022, Absolute = 0.5 },
            correlationId: null);

        ServedDuration(TempBasalToTreatmentMapper.ToTreatment(Persisted(stored))).Should().Be("4.962561716636022");
    }

    [Theory]
    [InlineData(30)]
    [InlineData(1)]
    [InlineData(120)]
    public void AnUploadedWholeMinuteDurationIsServedWhole(double minutes)
    {
        ServedDuration(Upload(minutes)).Should().Be(minutes.ToString("0"));
    }

    [Fact]
    public void AComputedFractionalDurationIsServedRounded()
    {
        var tempBasal = new V4Models.TempBasal
        {
            Id = Guid.CreateVersion7(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Start).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Start + 297_754).UtcDateTime,
            Rate = 0.5,
        };

        ServedDuration(TempBasalToTreatmentMapper.ToTreatment(tempBasal)).Should().Be("5");
    }

    [Fact]
    public void AnUploadedDurationTheRecordNoLongerMatchesIsServedRounded()
    {
        var stored = TreatmentDecomposer.MapToTempBasal(
            new Treatment { EventType = "Temp Basal", Mills = Start, Duration = 4.962561716636022, Absolute = 0.5 },
            correlationId: null);
        stored.EndTimestamp = stored.StartTimestamp.AddMinutes(2.6);

        ServedDuration(TempBasalToTreatmentMapper.ToTreatment(stored)).Should().Be("3");
    }

    [Fact]
    public void AnInsulinDerivedDurationIsNotKeptAsUploadedAndIsServedWhole()
    {
        var stored = TreatmentDecomposer.MapToTempBasal(
            new Treatment { EventType = "Temp Basal", Mills = Start, Insulin = 0.25, Rate = 1.0 },
            correlationId: null);

        (stored.AdditionalProperties?.ContainsKey("duration") ?? false).Should().BeFalse();
        ServedDuration(TempBasalToTreatmentMapper.ToTreatment(Persisted(stored))).Should().Be("15");
    }
}
