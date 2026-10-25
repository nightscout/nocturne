using Nocturne.Connectors.CareLink.Models;
using Nocturne.Connectors.CareLink.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using BolusType = Nocturne.Core.Models.V4.BolusType;

namespace Nocturne.Connectors.CareLink.Mappers;

/// <summary>
/// Maps the CareLink periodic-payload <c>markers</c> into typed v4 treatment records:
/// INSULIN → <see cref="Bolus"/>, MEAL → <see cref="CarbIntake"/>,
/// AUTO_BASAL_DELIVERY → <see cref="TempBasal"/> (<see cref="TempBasalOrigin.Algorithm"/>).
/// </summary>
/// <remarks>
/// Pure functions. The caller computes <c>pumpOffsetMs</c> once per sync via
/// <see cref="CareLinkTimestampParser.CalculatePumpOffsetMs"/> and every marker timestamp is parsed
/// against it, mirroring <see cref="CareLinkSensorGlucoseMapper"/>. Dedup keys are built from stable
/// fields only (never the array <c>index</c>, which is position-dependent and reshuffles per sync).
/// </remarks>
public static class CareLinkTreatmentMapper
{
    private const string InsulinMarker = "INSULIN";
    private const string MealMarker = "MEAL";
    private const string AutoBasalMarker = "AUTO_BASAL_DELIVERY";
    private const string Autocorrection = "AUTOCORRECTION";

    // Fallback span (minutes) for the most-recent auto-basal micro-dose, which has no following
    // marker to bound its end. The pump delivers an auto-basal roughly every 5 minutes.
    private const int DefaultAutoBasalIntervalMinutes = 5;

    public static List<Bolus> MapBoluses(CareLinkData data, double pumpOffsetMs)
    {
        if (data.Markers is not { Count: > 0 })
            return [];

        var deviceName = DeviceName(data);
        var now = DateTime.UtcNow;
        var results = new List<Bolus>();

        foreach (var marker in data.Markers.Where(m => IsType(m, InsulinMarker)))
        {
            var timestamp = CareLinkTimestampParser.ParseSgTimestamp(marker.DateTime, pumpOffsetMs);
            if (timestamp == null)
                continue;

            var programmed = (marker.ProgrammedFastAmount ?? 0) + (marker.ProgrammedExtendedAmount ?? 0);
            var delivered = (marker.DeliveredFastAmount ?? 0) + (marker.DeliveredExtendedAmount ?? 0);
            if (programmed <= 0 && delivered <= 0)
                continue;

            var mills = timestamp.Value.ToMills();
            var bolusType = MapBolusType(marker.BolusType);
            var automatic = string.Equals(marker.ActivationType, Autocorrection, StringComparison.OrdinalIgnoreCase);
            // Most INSULIN markers carry a stable pump event id; when one is absent, key on the bolus
            // content so two distinct id-less boluses at the same instant don't collapse into one.
            var idPart = marker.Id?.ToString() ?? FormattableString.Invariant($"{bolusType}:{delivered}");

            results.Add(new Bolus
            {
                Id = Guid.CreateVersion7(),
                Timestamp = timestamp.Value,
                Device = deviceName,
                DataSource = DataSources.CareLinkConnector,
                Insulin = delivered,
                Programmed = programmed,
                Delivered = delivered,
                BolusType = bolusType,
                Automatic = automatic,
                Kind = automatic ? BolusKind.Algorithm : BolusKind.Manual,
                Duration = bolusType is BolusType.Square or BolusType.Dual
                    ? marker.ProgrammedDuration
                    : null,
                SyncIdentifier = $"carelink:insulin:{idPart}:{mills}",
                CreatedAt = now,
                ModifiedAt = now,
            });
        }

        return results;
    }

    public static List<CarbIntake> MapCarbIntakes(CareLinkData data, double pumpOffsetMs)
    {
        if (data.Markers is not { Count: > 0 })
            return [];

        var deviceName = DeviceName(data);
        var now = DateTime.UtcNow;
        var results = new List<CarbIntake>();

        foreach (var marker in data.Markers.Where(m => IsType(m, MealMarker)))
        {
            var carbs = marker.Amount ?? 0;
            if (carbs <= 0)
                continue;

            var timestamp = CareLinkTimestampParser.ParseSgTimestamp(marker.DateTime, pumpOffsetMs);
            if (timestamp == null)
                continue;

            var mills = timestamp.Value.ToMills();

            results.Add(new CarbIntake
            {
                Id = Guid.CreateVersion7(),
                Timestamp = timestamp.Value,
                Device = deviceName,
                DataSource = DataSources.CareLinkConnector,
                Carbs = carbs,
                AbsorptionTime = null,
                SyncIdentifier = $"carelink:meal:{mills}:{carbs}",
                CreatedAt = now,
                ModifiedAt = now,
            });
        }

        return results;
    }

    public static List<TempBasal> MapTempBasals(CareLinkData data, double pumpOffsetMs)
    {
        if (data.Markers is not { Count: > 0 })
            return [];

        var deviceName = DeviceName(data);
        var now = DateTime.UtcNow;

        // Resolve each marker's timestamp up front and order chronologically so each micro-bolus
        // can be bounded by the next one's start.
        var basals = data.Markers
            .Where(m => IsType(m, AutoBasalMarker))
            .Select(m => (Marker: m, Timestamp: CareLinkTimestampParser.ParseSgTimestamp(m.DateTime, pumpOffsetMs)))
            .Where(x => x.Timestamp != null)
            .OrderBy(x => x.Timestamp!.Value)
            .ToList();

        var results = new List<TempBasal>(basals.Count);

        for (var i = 0; i < basals.Count; i++)
        {
            var (marker, timestamp) = basals[i];
            var start = timestamp!.Value;
            var next = i + 1 < basals.Count ? basals[i + 1].Timestamp!.Value : (DateTime?)null;

            var intervalMinutes = next.HasValue
                ? Math.Max((next.Value - start).TotalMinutes, 1)
                : DefaultAutoBasalIntervalMinutes;

            // bolusAmount is the units delivered over the interval; express as an hourly rate.
            var rate = (marker.BolusAmount ?? 0) * 60.0 / intervalMinutes;

            results.Add(new TempBasal
            {
                Id = Guid.CreateVersion7(),
                StartTimestamp = start,
                EndTimestamp = next ?? start.AddMinutes(DefaultAutoBasalIntervalMinutes),
                Device = deviceName,
                DataSource = DataSources.CareLinkConnector,
                Rate = rate,
                Origin = TempBasalOrigin.Algorithm,
                CreatedAt = now,
                ModifiedAt = now,
            });
        }

        return results;
    }

    private static bool IsType(CareLinkMarker marker, string type) =>
        string.Equals(marker.Type, type, StringComparison.OrdinalIgnoreCase);

    private static string DeviceName(CareLinkData data) =>
        $"CareLink {data.MedicalDeviceFamily ?? "Unknown"}";

    private static BolusType MapBolusType(string? bolusType) => bolusType?.ToUpperInvariant() switch
    {
        "SQUARE" => BolusType.Square,
        "DUAL" => BolusType.Dual,
        _ => BolusType.Normal,
    };
}
