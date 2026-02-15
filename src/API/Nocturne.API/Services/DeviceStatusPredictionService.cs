using Nocturne.API.Controllers.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services;

/// <summary>
/// Prediction service that reads predictions from the most recent DeviceStatus
/// (AAPS/Trio/Loop). Predictions are calculated by the AID system on the phone
/// and uploaded as part of the device status.
/// </summary>
public class DeviceStatusPredictionService : IPredictionService
{
    private readonly IPostgreSqlService _postgresService;
    private readonly ILogger<DeviceStatusPredictionService> _logger;

    public DeviceStatusPredictionService(
        IPostgreSqlService postgresService,
        ILogger<DeviceStatusPredictionService> logger)
    {
        _postgresService = postgresService;
        _logger = logger;
    }

    public async Task<GlucosePredictionResponse> GetPredictionsAsync(
        string? profileId = null,
        CancellationToken cancellationToken = default)
    {
        var statuses = await _postgresService.GetDeviceStatusAsync(
            count: 1,
            skip: 0,
            cancellationToken);

        var latest = statuses.FirstOrDefault();
        if (latest == null)
        {
            throw new InvalidOperationException("No device status data available for predictions");
        }

        // Try OpenAPS/AAPS format first (suggested has freshest predictions)
        var response = TryExtractFromOpenAps(latest);
        if (response != null)
            return response;

        // Try iOS Loop format
        response = TryExtractFromLoop(latest);
        if (response != null)
            return response;

        throw new InvalidOperationException(
            "No prediction data found in the most recent device status. " +
            "Ensure your AID system (AAPS, Trio, Loop) is uploading device status with prediction data.");
    }

    private GlucosePredictionResponse? TryExtractFromOpenAps(DeviceStatus status)
    {
        // Prefer enacted (confirmed delivered), fall back to suggested
        var command = status.OpenAps?.Enacted ?? status.OpenAps?.Suggested;
        if (command?.PredBGs == null)
            return null;

        var predBGs = command.PredBGs;
        var hasAnyCurve = predBGs.IOB != null || predBGs.ZT != null ||
                          predBGs.COB != null || predBGs.UAM != null;
        if (!hasAnyCurve)
            return null;

        // Use IOB curve as default (always present in AAPS/OpenAPS)
        var defaultCurve = predBGs.IOB ?? predBGs.COB ?? predBGs.ZT ?? predBGs.UAM;

        var timestamp = command.Timestamp != null
            ? DateTimeOffset.Parse(command.Timestamp)
            : DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "[Predictions] Extracted from OpenAPS device status: bg={Bg}, eventualBG={EventualBG}, " +
            "IOB curve={IobLen}, ZT curve={ZtLen}, COB curve={CobLen}, UAM curve={UamLen}",
            command.Bg, command.EventualBG,
            predBGs.IOB?.Count ?? 0, predBGs.ZT?.Count ?? 0,
            predBGs.COB?.Count ?? 0, predBGs.UAM?.Count ?? 0);

        return new GlucosePredictionResponse
        {
            Timestamp = timestamp,
            CurrentBg = command.Bg ?? 0,
            Delta = 0, // Not directly available in this format, could be parsed from tick
            EventualBg = command.EventualBG ?? command.Bg ?? 0,
            Iob = command.IOB ?? 0,
            Cob = command.COB ?? 0,
            SensitivityRatio = command.SensitivityRatio,
            IntervalMinutes = 5,
            Predictions = new PredictionCurves
            {
                Default = defaultCurve,
                IobOnly = predBGs.IOB,
                ZeroTemp = predBGs.ZT,
                Cob = predBGs.COB,
                Uam = predBGs.UAM,
            },
        };
    }

    private GlucosePredictionResponse? TryExtractFromLoop(DeviceStatus status)
    {
        var predicted = status.Loop?.Predicted;
        if (predicted?.Values == null || predicted.Values.Length == 0)
            return null;

        var values = predicted.Values.ToList();

        var timestamp = predicted.StartDate != null
            ? DateTimeOffset.Parse(predicted.StartDate)
            : DateTimeOffset.UtcNow;

        var currentBg = values.FirstOrDefault();
        var iob = status.Loop?.Iob?.Iob ?? 0;
        var cob = status.Loop?.Cob?.Cob ?? 0;

        _logger.LogInformation(
            "[Predictions] Extracted from Loop device status: bg={Bg}, points={PointCount}, iob={Iob}, cob={Cob}",
            currentBg, values.Count, iob, cob);

        return new GlucosePredictionResponse
        {
            Timestamp = timestamp,
            CurrentBg = currentBg,
            Delta = 0,
            EventualBg = values.LastOrDefault(),
            Iob = iob,
            Cob = cob,
            IntervalMinutes = 5,
            Predictions = new PredictionCurves
            {
                Default = values,
                // Loop only provides a single prediction curve
                IobOnly = null,
                ZeroTemp = null,
                Cob = null,
                Uam = null,
            },
        };
    }
}
