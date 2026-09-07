using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Glooko.Mappers;

/// <summary>
///     Maps the SSV2 <c>validic/biometric_measurements</c> feed to <see cref="HeartRate"/> records. Glooko
///     exposes heart rate <b>only</b> as the <c>restingHeartrate</c> field on this third-party biometric panel
///     (alongside cholesterol, blood pressure, SpO2, …); there is no continuous/time-series HR stream anywhere
///     in the SSV2 protocol. Records that carry no resting heart rate are skipped (the panel is mostly other
///     vitals). Keyed on the stable Glooko guid via a deterministic GUID <see cref="HeartRate.Id"/> so re-syncs
///     upsert in place; soft-delete aware.
/// </summary>
public class GlookoHeartRateMapper(string connectorSource, GlookoTimeMapper timeMapper, ILogger logger)
{
    private readonly string _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
    private readonly GlookoTimeMapper _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public List<HeartRate> MapSsv2BiometricMeasurements(IReadOnlyList<GlookoSsv2BiometricMeasurement> measurements)
    {
        var results = new List<HeartRate>();

        foreach (var m in measurements)
        {
            try
            {
                if (m.SoftDeleted || m.RestingHeartrate is not > 0) continue;

                var rawTimestamp = _timeMapper.GetRawGlookoDate(m.Timestamp ?? string.Empty, null);
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);

                var bpm = (int)Math.Round(m.RestingHeartrate.Value, MidpointRounding.AwayFromZero);
                if (bpm <= 0) continue;

                var key = !string.IsNullOrEmpty(m.Guid) ? m.Guid! : $"ssv2_biometric:{rawTimestamp.Ticks}:{bpm}";

                results.Add(new HeartRate
                {
                    Id = GlookoHealthIds.Derive("ssv2_biometric", key).ToString(),
                    Timestamp = correctedTimestamp,
                    Bpm = bpm,
                    Accuracy = 0,
                    Device = _connectorSource,
                    EnteredBy = _connectorSource,
                    DataSource = _connectorSource,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping SSV2 biometric measurement", _connectorSource);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} heart rates from SSV2 validic/biometric_measurements feed",
            _connectorSource, results.Count);

        return results;
    }
}
