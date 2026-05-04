using System.Text.Json;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.API.Services.Glucose;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// <see cref="IPredictionService"/> implementation that reads glucose predictions from the most recent
/// <see cref="ApsSnapshot"/> record. Predictions are calculated on the phone by the AID system
/// (AAPS, Trio, or Loop) and uploaded as part of the device status payload, then decomposed
/// into normalized <see cref="ApsSnapshot"/> columns.
/// </summary>
/// <seealso cref="IPredictionService"/>
public class DeviceStatusPredictionService : IPredictionService
{
    private readonly IApsSnapshotRepository _apsSnapshots;
    private readonly ILogger<DeviceStatusPredictionService> _logger;

    public DeviceStatusPredictionService(
        IApsSnapshotRepository apsSnapshots,
        ILogger<DeviceStatusPredictionService> logger)
    {
        _apsSnapshots = apsSnapshots;
        _logger = logger;
    }

    public async Task<GlucosePredictionResponse> GetPredictionsAsync(
        string? profileId = null,
        DateTimeOffset? asOf = null,
        CancellationToken cancellationToken = default)
    {
        // For replay we want the snapshot active at-or-before the anchor — the AID system
        // already wrote forward predictions historically, so picking the most recent snapshot
        // before `asOf` gives us the forecast the user actually had at that tick.
        var snapshots = await _apsSnapshots.GetAsync(
            from: null,
            to: asOf?.UtcDateTime,
            device: null,
            source: null,
            limit: 1,
            offset: 0,
            descending: true,
            ct: cancellationToken);

        var latest = snapshots.FirstOrDefault();
        if (latest == null)
        {
            throw new InvalidOperationException("No device status data available for predictions");
        }

        if (latest.AidAlgorithm == AidAlgorithm.Loop)
            return ExtractFromLoopSnapshot(latest);

        // OpenAPS / AAPS / Trio all use the same prediction column layout
        return ExtractFromOpenApsSnapshot(latest);
    }

    private GlucosePredictionResponse ExtractFromOpenApsSnapshot(ApsSnapshot snapshot)
    {
        var iobCurve = DeserializeCurve(snapshot.PredictedIobJson);
        var ztCurve = DeserializeCurve(snapshot.PredictedZtJson);
        var cobCurve = DeserializeCurve(snapshot.PredictedCobJson);
        var uamCurve = DeserializeCurve(snapshot.PredictedUamJson);
        var defaultCurve = DeserializeCurve(snapshot.PredictedDefaultJson) ?? iobCurve ?? cobCurve ?? ztCurve ?? uamCurve;

        if (defaultCurve == null)
        {
            throw new InvalidOperationException(
                "No prediction data found in the most recent APS snapshot. " +
                "Ensure your AID system (AAPS, Trio, Loop) is uploading device status with prediction data.");
        }

        var timestamp = snapshot.PredictedStartTimestamp.HasValue
            ? new DateTimeOffset(snapshot.PredictedStartTimestamp.Value, TimeSpan.Zero)
            : new DateTimeOffset(snapshot.Timestamp, TimeSpan.Zero);

        _logger.LogInformation(
            "[Predictions] Extracted from APS snapshot ({Algorithm}): bg={Bg}, eventualBG={EventualBG}, " +
            "IOB curve={IobLen}, ZT curve={ZtLen}, COB curve={CobLen}, UAM curve={UamLen}",
            snapshot.AidAlgorithm, snapshot.CurrentBg, snapshot.EventualBg,
            iobCurve?.Count ?? 0, ztCurve?.Count ?? 0,
            cobCurve?.Count ?? 0, uamCurve?.Count ?? 0);

        return new GlucosePredictionResponse
        {
            Timestamp = timestamp,
            CurrentBg = snapshot.CurrentBg ?? 0,
            Delta = 0,
            EventualBg = snapshot.EventualBg ?? snapshot.CurrentBg ?? 0,
            Iob = snapshot.Iob ?? 0,
            Cob = snapshot.Cob ?? 0,
            SensitivityRatio = snapshot.SensitivityRatio,
            IntervalMinutes = 5,
            Predictions = new PredictionCurves
            {
                Default = defaultCurve,
                IobOnly = iobCurve,
                ZeroTemp = ztCurve,
                Cob = cobCurve,
                Uam = uamCurve,
            },
        };
    }

    private GlucosePredictionResponse ExtractFromLoopSnapshot(ApsSnapshot snapshot)
    {
        var values = DeserializeCurve(snapshot.PredictedDefaultJson);
        if (values == null || values.Count == 0)
        {
            throw new InvalidOperationException(
                "No prediction data found in the most recent APS snapshot. " +
                "Ensure your AID system (AAPS, Trio, Loop) is uploading device status with prediction data.");
        }

        var currentBg = values.FirstOrDefault();

        var timestamp = snapshot.PredictedStartTimestamp.HasValue
            ? new DateTimeOffset(snapshot.PredictedStartTimestamp.Value, TimeSpan.Zero)
            : new DateTimeOffset(snapshot.Timestamp, TimeSpan.Zero);

        _logger.LogInformation(
            "[Predictions] Extracted from APS snapshot (Loop): bg={Bg}, points={PointCount}, iob={Iob}, cob={Cob}",
            currentBg, values.Count, snapshot.Iob, snapshot.Cob);

        return new GlucosePredictionResponse
        {
            Timestamp = timestamp,
            CurrentBg = currentBg,
            Delta = 0,
            EventualBg = values.LastOrDefault(),
            Iob = snapshot.Iob ?? 0,
            Cob = snapshot.Cob ?? 0,
            SensitivityRatio = snapshot.SensitivityRatio,
            IntervalMinutes = 5,
            Predictions = new PredictionCurves
            {
                Default = values,
                IobOnly = null,
                ZeroTemp = null,
                Cob = null,
                Uam = null,
            },
        };
    }

    private static List<double>? DeserializeCurve(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            var values = JsonSerializer.Deserialize<List<double?>>(json);
            return values?.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
