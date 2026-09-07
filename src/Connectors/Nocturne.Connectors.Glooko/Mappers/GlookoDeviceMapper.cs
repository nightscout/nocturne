using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Glooko.Mappers;

/// <summary>
///     Maps the SSV2 hardware-inventory feeds (<c>pumps</c> and <c>cgm_devices</c>) to V4
///     <see cref="PatientDevice"/> records — the patient's declared pump and CGM hardware. The two feeds
///     share an identical record shape (<see cref="GlookoSsv2Device"/>); the only per-feed difference is
///     which <c>properties</c> key holds the human-readable model, so the device <see cref="DeviceCategory"/>
///     is supplied by the caller (decided by which feed the record came from) rather than inferred.
/// </summary>
/// <remarks>
///     Soft-deleted records are skipped. The stable identity is Glooko's <c>guid</c> (carried in
///     <see cref="PatientDevice.SerialNumber"/> when no real serial exists, and used to derive the
///     deterministic <see cref="PatientDevice.Id"/>) so re-syncs upsert rather than duplicate.
///     <see cref="PatientDevice.AidAlgorithm"/> is left null: the inventory feed never identifies the
///     control algorithm, and guessing it from the model would be unreliable.
/// </remarks>
public class GlookoDeviceMapper
{
    private readonly string _connectorSource;
    private readonly GlookoTimeMapper _timeMapper;
    private readonly ILogger _logger;

    public GlookoDeviceMapper(string connectorSource, GlookoTimeMapper timeMapper, ILogger logger)
    {
        _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
        _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Maps the <c>pumps</c> feed to <see cref="PatientDevice"/>s categorised as InsulinPump.</summary>
    public List<PatientDevice> TransformPumpsToPatientDevices(IEnumerable<GlookoSsv2Device>? pumps)
        => Transform(pumps, DeviceCategory.InsulinPump, "pumps");

    /// <summary>Maps the <c>cgm_devices</c> feed to <see cref="PatientDevice"/>s categorised as CGM.</summary>
    public List<PatientDevice> TransformCgmDevicesToPatientDevices(IEnumerable<GlookoSsv2Device>? devices)
        => Transform(devices, DeviceCategory.CGM, "cgm_devices");

    private List<PatientDevice> Transform(
        IEnumerable<GlookoSsv2Device>? devices, DeviceCategory category, string feed)
    {
        var results = new List<PatientDevice>();
        if (devices == null) return results;

        var skipped = 0;
        foreach (var device in devices)
        {
            if (device.SoftDeleted)
            {
                skipped++;
                continue;
            }

            // Identity must be stable across re-syncs so we upsert rather than duplicate. Glooko's guid is
            // the only durable key (serials are sometimes absent or composite). Records with neither a guid
            // nor a serial can't be keyed safely, so they're skipped rather than guessed.
            var key = device.Guid;
            if (string.IsNullOrWhiteSpace(key))
                key = device.SerialNumber;
            if (string.IsNullOrWhiteSpace(key))
            {
                skipped++;
                continue;
            }

            var now = DateTime.UtcNow;
            results.Add(new PatientDevice
            {
                // Deterministic id from the connector source + stable key so re-syncs target the same row.
                Id = DeriveDeviceId(category, key),
                DeviceCategory = category,
                Manufacturer = device.Brand ?? string.Empty,
                Model = ResolveModel(device, category),
                SerialNumber = ResolveSerial(device),
                IsCurrent = device.ActivelyUploaded,
                StartDate = null,
                EndDate = null,
                Notes = null,
                CreatedAt = now,
                ModifiedAt = ParseLastSync(device.LastSyncTimestamp) ?? now,
            });
        }

        if (skipped > 0)
            _logger.LogInformation(
                "[{ConnectorSource}] Skipped {Count} {Feed} records (soft-deleted or unkeyable)",
                _connectorSource, skipped, feed);

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} patient devices from SSV2 {Feed}",
            _connectorSource, results.Count, feed);

        return results;
    }

    /// <summary>
    ///     Prefers the precise per-feed model (<c>properties.pumpModel</c> / <c>properties.cgmModel</c>)
    ///     over the coarser record-level <c>model</c> (the brand's product line). Falls back through both.
    /// </summary>
    private static string ResolveModel(GlookoSsv2Device device, DeviceCategory category)
    {
        var preferred = category == DeviceCategory.InsulinPump
            ? device.Properties?.PumpModel
            : device.Properties?.CgmModel;

        return Coalesce(preferred, device.Model) ?? string.Empty;
    }

    /// <summary>
    ///     Uses the real serial when present; otherwise carries the guid so the record still has a unique,
    ///     stable identifier (the inventory feed often omits a parseable serial).
    /// </summary>
    private static string? ResolveSerial(GlookoSsv2Device device)
        => Coalesce(device.SerialNumber, device.Guid);

    /// <summary>
    ///     Deterministic UUIDv5 (SHA-1, name-based) from category + stable key, so a given Glooko device
    ///     always maps to the same <see cref="PatientDevice.Id"/> across syncs. <see cref="PatientDevice"/>
    ///     has no string sync-identifier field (unlike the time-series records), so the deterministic Id is
    ///     the upsert key the publisher will key on once a publish path exists.
    /// </summary>
    private static Guid DeriveDeviceId(DeviceCategory category, string key)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"glooko-patient-device:{category}:{key}"));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant
        return new Guid(bytes);
    }

    private DateTime? ParseLastSync(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
            return null;

        // RoundtripKind keeps Glooko's fake-UTC wall-clock intact so the time mapper can correct it,
        // consistent with the other Glooko mappers.
        if (!DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            _logger.LogWarning(
                "[{ConnectorSource}] Failed to parse device lastSyncTimestamp '{Timestamp}'",
                _connectorSource, timestamp);
            return null;
        }

        return _timeMapper.GetCorrectedGlookoTime(parsed);
    }

    private static string? Coalesce(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
