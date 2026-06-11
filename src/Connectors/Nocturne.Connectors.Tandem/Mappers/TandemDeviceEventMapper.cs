using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Tandem.Mappers;

/// <summary>
/// Maps Tandem cartridge/cannula/tubing fills, CGM session start/join/stop, and pump
/// suspend/resume events to <see cref="DeviceEvent"/> records. Mirrors <c>tconnectsync</c>'s
/// <c>process_cartridge.py</c>, <c>process_cgm_start_join_stop.py</c>,
/// <c>process_basal_suspension.py</c> and <c>process_basal_resume.py</c>.
/// </summary>
public sealed class TandemDeviceEventMapper(ILogger logger, TandemTimeResolver time)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TandemTimeResolver _time = time ?? throw new ArgumentNullException(nameof(time));

    public List<DeviceEvent> Map(IEnumerable<TandemPumpEvent> events)
    {
        var now = DateTime.UtcNow;
        var records = new List<DeviceEvent>();

        foreach (var ev in events)
        {
            var mapped = ev.Name switch
            {
                "LID_CARTRIDGE_FILLED" => (DeviceEventType.ReservoirChange,
                    "Cartridge Filled" + Volume(ev.Num("V2Volume"), "filled")),
                "LID_CANNULA_FILLED" => (DeviceEventType.CannulaChange,
                    "Cannula Filled" + Volume(ev.Num("PrimeSize"), "primed")),
                "LID_TUBING_FILLED" => (DeviceEventType.TubePriming,
                    "Tubing Filled" + Volume(ev.Num("PrimeSize"), "primed")),
                "LID_PUMPING_SUSPENDED" => (DeviceEventType.PumpSuspend,
                    ev.EnumName("SuspendReason") ?? "Pump suspended"),
                "LID_PUMPING_RESUMED" => (DeviceEventType.PumpResume, "Basal resumed"),
                _ when TandemEventClasses.CgmSessionStart.Contains(ev.Name) =>
                    (DeviceEventType.SensorStart, "CGM Session Started"),
                _ when TandemEventClasses.CgmSessionJoin.Contains(ev.Name) =>
                    (DeviceEventType.SensorStart, "CGM Session Joined"),
                _ when TandemEventClasses.CgmSessionStop.Contains(ev.Name) =>
                    (DeviceEventType.SensorStop, "CGM Session Stopped"),
                _ => ((DeviceEventType, string)?)null,
            };

            if (mapped == null)
                continue;

            records.Add(new DeviceEvent
            {
                Id = Guid.CreateVersion7(),
                Timestamp = _time.ToUtc(ev.RawTimestampSeconds),
                UtcOffset = _time.OffsetMinutes,
                Device = TandemMapHelpers.Source,
                DataSource = TandemMapHelpers.Source,
                SyncIdentifier = $"tandem_devent_{ev.SeqNum}",
                LegacyId = $"tandem_devent_{ev.SeqNum}",
                EventType = mapped.Value.Item1,
                Notes = mapped.Value.Item2,
                CreatedAt = now,
                ModifiedAt = now,
            });
        }

        _logger.LogDebug("Mapped {Count} Tandem device events", records.Count);
        return records;
    }

    private static string Volume(double? units, string verb) =>
        units is > 0 ? $" ({units.Value.ToString("0.##", CultureInfo.InvariantCulture)}u {verb})" : string.Empty;
}
