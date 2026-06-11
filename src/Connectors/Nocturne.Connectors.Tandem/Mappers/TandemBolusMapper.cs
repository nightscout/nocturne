using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Tandem.Mappers;

/// <summary>The bolus-related records decomposed from a set of Tandem bolus events.</summary>
public sealed class TandemBolusResult
{
    public List<Bolus> Boluses { get; } = [];
    public List<CarbIntake> CarbIntakes { get; } = [];
    public List<BolusCalculation> BolusCalculations { get; } = [];
}

/// <summary>
/// Reassembles Tandem's multi-message bolus events (request messages 1–3, completion, and the
/// extended <c>BOLEX</c> completion) into <see cref="Bolus"/>, <see cref="CarbIntake"/> and
/// <see cref="BolusCalculation"/> records, keyed by the pump's bolus id. Mirrors
/// <c>tconnectsync</c>'s <c>process_bolus.py</c>.
/// </summary>
public sealed class TandemBolusMapper(ILogger logger, TandemTimeResolver time)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TandemTimeResolver _time = time ?? throw new ArgumentNullException(nameof(time));

    public TandemBolusResult Map(IEnumerable<TandemPumpEvent> events)
    {
        var result = new TandemBolusResult();

        // Group every bolus message by its bolus id so the completion event can pull in the
        // matching request/extended messages.
        var byId = new Dictionary<long, Dictionary<string, TandemPumpEvent>>();
        foreach (var ev in events)
        {
            var bolusId = ev.Raw("BolusID");
            if (bolusId == null)
                continue;
            if (!byId.TryGetValue(bolusId.Value, out var group))
                byId[bolusId.Value] = group = new Dictionary<string, TandemPumpEvent>();
            group[ev.Name] = ev;
        }

        var now = DateTime.UtcNow;

        foreach (var (bolusId, group) in byId)
        {
            if (!group.TryGetValue("LID_BOLUS_COMPLETED", out var completed))
                continue;

            group.TryGetValue("LID_BOLUS_REQUESTED_MSG1", out var msg1);
            group.TryGetValue("LID_BOLUS_REQUESTED_MSG2", out var msg2);
            group.TryGetValue("LID_BOLUS_REQUESTED_MSG3", out var msg3);
            group.TryGetValue("LID_BOLEX_COMPLETED", out var bolex);

            var timestamp = _time.ToUtc(completed.RawTimestampSeconds);
            var correlationId = Guid.CreateVersion7();

            var delivered = completed.Num("InsulinDelivered") ?? 0;
            if (bolex != null)
                delivered += bolex.Num("InsulinDelivered") ?? 0;
            var requested = completed.Num("InsulinRequested") ?? delivered;

            // Record every contributing event's sequence number for provenance, as process_bolus.py does.
            var seqNums = string.Join(",", new[] { completed, msg1, msg2, msg3 }
                .Where(e => e != null)
                .Select(e => e!.SeqNum.ToString()));

            var options = msg2?.EnumName("Options");
            var duration = msg2?.Num("Duration") ?? 0;
            var standardPercent = msg2?.Num("StandardPercent") ?? 100;
            var isExtended = bolex != null || duration > 0;
            var isAutomatic = options is "Automatic Bolus" or "Eating Soon Automatic Bolus";

            var bolus = new Bolus
            {
                Id = Guid.CreateVersion7(),
                Timestamp = timestamp,
                UtcOffset = _time.OffsetMinutes,
                Device = TandemMapHelpers.Source,
                DataSource = TandemMapHelpers.Source,
                CorrelationId = correlationId,
                SyncIdentifier = $"tandem_bolus_{bolusId}",
                LegacyId = $"tandem_bolus_{bolusId}",
                Insulin = TandemMapHelpers.Round2(delivered),
                Programmed = TandemMapHelpers.Round2(requested),
                Delivered = TandemMapHelpers.Round2(delivered),
                BolusType = isExtended ? (standardPercent > 0 ? BolusType.Dual : BolusType.Square) : BolusType.Normal,
                Automatic = isAutomatic,
                Kind = isAutomatic ? BolusKind.Algorithm : BolusKind.Manual,
                Duration = duration > 0 ? duration : null,
                PumpRecordId = seqNums,
                CreatedAt = now,
                ModifiedAt = now,
                AdditionalProperties = BuildNotes(options, msg2),
            };
            result.Boluses.Add(bolus);

            var carbAmount = msg1?.Num("CarbAmount") ?? 0;
            if (carbAmount > 0)
                result.CarbIntakes.Add(new CarbIntake
                {
                    Id = Guid.CreateVersion7(),
                    Timestamp = timestamp,
                    UtcOffset = _time.OffsetMinutes,
                    Device = TandemMapHelpers.Source,
                    DataSource = TandemMapHelpers.Source,
                    CorrelationId = correlationId,
                    SyncIdentifier = $"tandem_carb_{bolusId}",
                    LegacyId = $"tandem_carb_{bolusId}",
                    Carbs = carbAmount,
                    CreatedAt = now,
                    ModifiedAt = now,
                });

            if (msg1 != null || msg3 != null)
            {
                var bg = msg1?.Num("BG") ?? 0;
                result.BolusCalculations.Add(new BolusCalculation
                {
                    Id = Guid.CreateVersion7(),
                    Timestamp = timestamp,
                    UtcOffset = _time.OffsetMinutes,
                    Device = TandemMapHelpers.Source,
                    DataSource = TandemMapHelpers.Source,
                    CorrelationId = correlationId,
                    LegacyId = $"tandem_boluscalc_{bolusId}",
                    CarbInput = carbAmount > 0 ? carbAmount : null,
                    BloodGlucoseInput = bg > 0 ? bg : null,
                    InsulinOnBoard = msg1?.Num("IOB"),
                    CarbRatio = msg1?.Num("CarbRatio"),
                    InsulinRecommendation = msg3?.Num("TotalBolusSize"),
                    InsulinRecommendationForCarbs = msg3?.Num("FoodBolusSize"),
                    InsulinProgrammed = TandemMapHelpers.Round2(requested),
                    CreatedAt = now,
                    ModifiedAt = now,
                });
            }
        }

        _logger.LogDebug(
            "Mapped {Boluses} boluses, {Carbs} carb intakes, {Calcs} bolus calculations from Tandem",
            result.Boluses.Count, result.CarbIntakes.Count, result.BolusCalculations.Count);
        return result;
    }

    private static Dictionary<string, object?>? BuildNotes(string? options, TandemPumpEvent? msg2)
    {
        var notes = new List<string>();
        if (!string.IsNullOrEmpty(options))
            notes.Add(options);
        if (msg2?.EnumName("UserOverride")?.StartsWith("\"Yes\"", StringComparison.Ordinal) == true)
            notes.Add("(Override)");
        if (msg2?.EnumName("DeclinedCorrection")?.StartsWith("\"Yes\"", StringComparison.Ordinal) == true)
            notes.Add("(Declined Correction)");

        return notes.Count > 0
            ? new Dictionary<string, object?> { ["notes"] = string.Join(" ", notes) }
            : null;
    }
}
