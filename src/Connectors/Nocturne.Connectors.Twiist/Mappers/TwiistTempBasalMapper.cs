using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Twiist.Models;
using Nocturne.Connectors.Twiist.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Twiist.Mappers;

/// <summary>
/// Maps Twiist basal data to V4 TempBasal records from two sources: the insulin-delivery blob
/// (the loop-enacted rate each interval) and the Suspend/Resume events in the insulin history
/// (actual pump suspensions). A zero blob rate is a loop low-temp, not a suspension — only Suspend
/// events map to <see cref="TempBasalOrigin.Suspended"/>.
/// </summary>
public class TwiistTempBasalMapper(ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Maps insulin-delivery records to TempBasal records using the scheduled basal rate to convert
    /// each interval's delta into an absolute rate. All are <see cref="TempBasalOrigin.Algorithm"/>
    /// — a zero rate here is an algorithm low-temp, not a pump suspension.
    /// </summary>
    public IEnumerable<TempBasal> MapTempBasals(string? insulinDeliveryBlob, double scheduledRate)
    {
        var records = TwiistBlobDecoder.ParseInsulinDeliveryRecords(insulinDeliveryBlob);
        if (records.Count == 0)
            return [];

        var now = DateTime.UtcNow;

        return records
            .Where(r => r.End > r.Start)
            .Select(r => new TempBasal
            {
                Id = Guid.CreateVersion7(),
                StartTimestamp = r.Start,
                EndTimestamp = r.End,
                Rate = Math.Max(0, scheduledRate + r.DeltaUhr),
                ScheduledRate = scheduledRate,
                Origin = TempBasalOrigin.Algorithm,
                Device = DataSources.TwiistConnector,
                DataSource = DataSources.TwiistConnector,
                LegacyId = $"twiist_tempbasal_{r.Start.Ticks}",
                CreatedAt = now,
                ModifiedAt = now
            })
            .OrderBy(t => t.StartTimestamp)
            .ToList();
    }

    /// <summary>
    /// Maps Suspend/Resume dose events to zero-rate <see cref="TempBasalOrigin.Suspended"/> spans:
    /// each Suspend opens a span that the next Resume closes. A Suspend with no following Resume is
    /// still active, so its span is left open (null end).
    /// </summary>
    public IEnumerable<TempBasal> MapSuspensions(List<TwiistInsulinDose>? doses)
    {
        if (doses == null || doses.Count == 0)
            return [];

        var now = DateTime.UtcNow;
        var spans = new List<TempBasal>();
        DateTime? suspendStart = null;

        foreach (var dose in doses
                     .Where(d => d.StartDate != null && (IsType(d, "Suspend") || IsType(d, "Resume")))
                     .OrderBy(d => d.StartDate!.Value))
        {
            if (IsType(dose, "Suspend"))
                suspendStart ??= dose.StartDate!.Value.ToUniversalTime();
            else if (suspendStart != null)
            {
                spans.Add(BuildSuspension(suspendStart.Value, dose.StartDate!.Value.ToUniversalTime(), now));
                suspendStart = null;
            }
        }

        if (suspendStart != null)
            spans.Add(BuildSuspension(suspendStart.Value, null, now));

        return spans;
    }

    private static bool IsType(TwiistInsulinDose dose, string type) =>
        string.Equals(dose.DoseType, type, StringComparison.OrdinalIgnoreCase);

    private static TempBasal BuildSuspension(DateTime start, DateTime? end, DateTime now) => new()
    {
        Id = Guid.CreateVersion7(),
        StartTimestamp = start,
        EndTimestamp = end,
        Rate = 0,
        Origin = TempBasalOrigin.Suspended,
        Device = DataSources.TwiistConnector,
        DataSource = DataSources.TwiistConnector,
        LegacyId = $"twiist_suspend_{start.Ticks}",
        CreatedAt = now,
        ModifiedAt = now
    };

    /// <summary>
    /// Parses the scheduled basal rate from the package details. The follower API returns it as a
    /// stringified number; returns 0 when absent or unparseable (deltas then map to absolute rate
    /// directly, clamped at zero).
    /// </summary>
    public double ParseScheduledRate(string? basalRateUnitsPerHour)
    {
        if (string.IsNullOrWhiteSpace(basalRateUnitsPerHour))
            return 0;

        if (double.TryParse(basalRateUnitsPerHour, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
            return rate;

        _logger.LogWarning("[twiist] Could not parse scheduled basal rate '{Value}'", basalRateUnitsPerHour);
        return 0;
    }
}
