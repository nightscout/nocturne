using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Twiist.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Twiist.Mappers;

/// <summary>
/// Maps the Twiist insulin-delivery blob to V4 TempBasal records.
/// Each blob record is a delivery interval carrying the deviation from the scheduled basal rate;
/// the absolute rate is the scheduled rate plus that delta. Twiist is a closed-loop system, so
/// records originate from the loop algorithm (or a suspension when the rate is zero).
/// </summary>
public class TwiistTempBasalMapper(ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Maps insulin-delivery records to TempBasal records using the scheduled basal rate to convert
    /// each interval's delta into an absolute rate.
    /// </summary>
    public IEnumerable<TempBasal> MapTempBasals(string? insulinDeliveryBlob, double scheduledRate)
    {
        var records = TwiistBlobDecoder.ParseInsulinDeliveryRecords(insulinDeliveryBlob);
        if (records.Count == 0)
            return [];

        var now = DateTime.UtcNow;

        return records
            .Where(r => r.End > r.Start)
            .Select(r =>
            {
                var rate = Math.Max(0, scheduledRate + r.DeltaUhr);
                return new TempBasal
                {
                    Id = Guid.CreateVersion7(),
                    StartTimestamp = r.Start,
                    EndTimestamp = r.End,
                    Rate = rate,
                    ScheduledRate = scheduledRate,
                    Origin = rate < 0.0005 ? TempBasalOrigin.Suspended : TempBasalOrigin.Algorithm,
                    Device = DataSources.TwiistConnector,
                    DataSource = DataSources.TwiistConnector,
                    LegacyId = $"twiist_tempbasal_{r.Start.Ticks}",
                    CreatedAt = now,
                    ModifiedAt = now
                };
            })
            .OrderBy(t => t.StartTimestamp)
            .ToList();
    }

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
