using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Tandem.Mappers;

/// <summary>
/// Maps the most recent Tandem daily-basal event in a window to a <see cref="DeviceStatus"/> snapshot
/// carrying pump battery and IOB. Mirrors <c>tconnectsync</c>'s <c>process_device_status.py</c>,
/// including its battery-charge-percent computation.
/// </summary>
public sealed class TandemDeviceStatusMapper(ILogger logger, TandemTimeResolver time)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TandemTimeResolver _time = time ?? throw new ArgumentNullException(nameof(time));

    public List<DeviceStatus> Map(IEnumerable<TandemPumpEvent> events)
    {
        var latest = events
            .Where(e => e.Name == "LID_DAILY_BASAL")
            .OrderByDescending(e => e.RawTimestampSeconds)
            .FirstOrDefault();

        if (latest == null)
            return [];

        var utc = _time.ToUtc(latest.RawTimestampSeconds);
        var batteryPercent = BatteryChargePercent(latest);
        var voltage = (latest.Num("batteryLipoMilliVolts") ?? 0) / 1000.0;

        var status = new DeviceStatus
        {
            Device = TandemMapHelpers.Source,
            Mills = TandemMapHelpers.ToMills(utc),
            CreatedAt = utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            UtcOffset = _time.OffsetMinutes,
            Pump = new PumpStatus
            {
                Manufacturer = "Tandem",
                Clock = utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                Battery = new PumpBattery
                {
                    Percent = (int)Math.Round(100 * batteryPercent),
                    Voltage = voltage,
                },
                Iob = new PumpIob { Iob = latest.Num("iob") },
            },
        };

        _logger.LogDebug("Mapped Tandem device status (battery {Percent:P0})", batteryPercent);
        return [status];
    }

    /// <summary>
    /// Battery charge percent from the MSB/LSB raw fields:
    /// (256·(MSB−14) + LSB) / (3·256). Ported verbatim from tconnectsync's transform.
    /// </summary>
    private static double BatteryChargePercent(TandemPumpEvent ev)
    {
        var msb = ev.Raw("batteryChargePercentMSBRaw") ?? 0;
        var lsb = ev.Raw("batteryChargePercentLSBRaw") ?? 0;
        return (256.0 * (msb - 14) + lsb) / (3.0 * 256);
    }
}
