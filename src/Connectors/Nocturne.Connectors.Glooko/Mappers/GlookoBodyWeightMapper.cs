using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;
using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.Connectors.Glooko.Mappers;

/// <summary>
///     Maps the two SSV2 weight feeds to <see cref="BodyWeight"/> records:
///     <c>weights</c> (manual / HealthKit — <c>value</c> in <b>grams</b>) and
///     <c>validic/weights</c> (third-party — <c>weight</c> already in <b>kilograms</b>, with optional BMI).
///     Both normalize to <see cref="BodyWeight.WeightKg"/> (kilograms). Records are keyed on their stable
///     Glooko guid via a deterministic GUID <see cref="BodyWeight.Id"/> so re-syncs upsert in place rather
///     than duplicate; soft-delete aware; non-positive weights skipped.
/// </summary>
public class GlookoBodyWeightMapper(string connectorSource, GlookoTimeMapper timeMapper, ILogger logger)
{
    private readonly string _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
    private readonly GlookoTimeMapper _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Maps the manual/HealthKit <c>weights</c> feed. <c>value</c> is grams → divided by 1000 for kg.</summary>
    public List<BodyWeight> MapSsv2Weights(IReadOnlyList<GlookoSsv2Weight> weights)
    {
        var results = new List<BodyWeight>();

        foreach (var w in ConnectorFaults.NonNullRecords(weights, _logger, _connectorSource, "SSV2 weight"))
        {
            try
            {
                if (w.SoftDeleted) continue;

                // value is grams (e.g. 86700 = 86.7 kg).
                var weightKg = (decimal)(w.Value / 1000.0);
                if (weightKg <= 0) continue;

                results.Add(BuildWeight(w.Timestamp, weightKg, bodyFat: null, "ssv2_weight", w.Guid));
            }
            catch (Exception ex) when (ConnectorFaults.IsMalformedRecord(ex))
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Skipped malformed SSV2 weight {Guid}", _connectorSource, w.Guid);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} body weights from SSV2 weights feed", _connectorSource, results.Count);

        return results;
    }

    /// <summary>Maps the third-party <c>validic/weights</c> feed. <c>weight</c> is already kilograms — no conversion.</summary>
    public List<BodyWeight> MapSsv2ValidicWeights(IReadOnlyList<GlookoSsv2ValidicWeight> weights)
    {
        var results = new List<BodyWeight>();

        foreach (var w in ConnectorFaults.NonNullRecords(weights, _logger, _connectorSource, "SSV2 Validic weight"))
        {
            try
            {
                if (w.SoftDeleted || w.Weight is not > 0) continue;

                var weightKg = (decimal)w.Weight.Value;

                results.Add(BuildWeight(w.Timestamp, weightKg, bodyFat: null, "ssv2_validic_weight", w.Guid));
            }
            catch (Exception ex) when (ConnectorFaults.IsMalformedRecord(ex))
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Skipped malformed SSV2 validic weight {Guid}", _connectorSource, w.Guid);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} body weights from SSV2 validic/weights feed", _connectorSource, results.Count);

        return results;
    }

    private BodyWeight BuildWeight(string? timestamp, decimal weightKg, decimal? bodyFat, string eventType, string? guid)
    {
        var rawTimestamp = _timeMapper.GetRawGlookoDate(timestamp ?? string.Empty, null);
        var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);
        var mills = correctedTimestamp.ToMills();

        // Stable upsert key: a deterministic GUID derived from the Glooko guid (raw-timestamp hash fallback).
        // Carried in Id so the publisher's get-by-id round-trips to the entity PK and re-syncs update in place.
        var key = !string.IsNullOrEmpty(guid) ? guid! : $"{eventType}:{rawTimestamp.Ticks}:{weightKg}";

        return new BodyWeight
        {
            Id = GlookoHealthIds.Derive(eventType, key).ToString(),
            Mills = mills,
            WeightKg = weightKg,
            BodyFatPercent = bodyFat,
            Device = _connectorSource,
            EnteredBy = _connectorSource,
            DataSource = _connectorSource,
        };
    }
}

/// <summary>
///     Shared deterministic-id derivation for the connector's health/biometric records (BodyWeight,
///     StepCount, HeartRate). The upsert key is a real GUID carried in the model's <c>Id</c>, which
///     round-trips through the entity mapper to the primary key, so rows stored before a sync identifier
///     existed keep matching. The entity <c>OriginalId</c> column only accepts 24-char Mongo ObjectIds.
/// </summary>
internal static class GlookoHealthIds
{
    /// <summary>UUIDv5 (SHA-1, name-based) from connector namespace + record type + stable key.</summary>
    public static Guid Derive(string eventType, string key)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"glooko-health:{eventType}:{key}"));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant
        return new Guid(bytes);
    }
}
