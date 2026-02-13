using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Glooko.Mappers;

public class GlookoTreatmentMapper
{
    private readonly ITreatmentClassificationService _classificationService;
    private readonly string _connectorSource;
    private readonly ILogger _logger;
    private readonly GlookoTimeMapper _timeMapper;

    public GlookoTreatmentMapper(
        string connectorSource,
        ITreatmentClassificationService classificationService,
        GlookoTimeMapper timeMapper,
        ILogger logger)
    {
        _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
        _classificationService =
            classificationService ?? throw new ArgumentNullException(nameof(classificationService));
        _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public List<Treatment> TransformBatchDataToTreatments(GlookoBatchData batchData)
    {
        var treatments = new List<Treatment>();

        try
        {
            if (batchData.Foods != null)
                foreach (var food in batchData.Foods)
                {
                    var treatment = new Treatment();
                    var foodDate = _timeMapper.GetRawGlookoDate(food.Timestamp, food.PumpTimestamp);
                    var carbs = food.Carbs > 0 ? food.Carbs : food.CarbohydrateGrams;

                    var eventType = _classificationService.ClassifyTreatment(carbs, null);
                    treatment.EventType = eventType;
                    var correctedFoodTime = _timeMapper
                        .GetCorrectedGlookoTime(foodDate)
                        .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    treatment.CreatedAt = correctedFoodTime;
                    treatment.EventTime = correctedFoodTime;
                    treatment.Id = GenerateTreatmentId(
                        eventType,
                        foodDate,
                        $"carbs:{carbs}"
                    );

                    treatment.Carbs = carbs;
                    treatment.AdditionalProperties = JsonSerializer.Deserialize<
                        Dictionary<string, object>
                    >(JsonSerializer.Serialize(food));
                    treatment.DataSource = _connectorSource;
                    treatments.Add(treatment);
                }

            if (batchData.NormalBoluses != null)
                foreach (var bolus in batchData.NormalBoluses)
                {
                    var bolusDate = _timeMapper.GetRawGlookoDate(
                        bolus.Timestamp,
                        bolus.PumpTimestamp
                    );
                    var carbs = bolus.CarbsInput > 0 ? bolus.CarbsInput : (double?)null;
                    var insulin = bolus.InsulinDelivered > 0 ? bolus.InsulinDelivered : (double?)null;

                    var eventType = _classificationService.ClassifyTreatment(carbs, insulin);

                    var treatment = new Treatment
                    {
                        Id = GenerateTreatmentId(
                            eventType,
                            bolusDate,
                            $"insulin:{bolus.InsulinDelivered}_carbs:{bolus.CarbsInput}"
                        ),
                        EventType = eventType,
                        CreatedAt = _timeMapper
                            .GetCorrectedGlookoTime(bolusDate)
                            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        Insulin = insulin,
                        Carbs = carbs,
                        DataSource = _connectorSource
                    };
                    treatments.Add(treatment);
                }

            if (batchData.ScheduledBasals != null)
                foreach (var basal in batchData.ScheduledBasals)
                {
                    var basalDate = _timeMapper.GetRawGlookoDate(
                        basal.Timestamp,
                        basal.PumpTimestamp
                    );
                    var treatment = new Treatment
                    {
                        Id = GenerateTreatmentId(
                            "Scheduled Basal",
                            basalDate,
                            $"rate:{basal.Rate}"
                        ),
                        EventType = "Scheduled Basal",
                        CreatedAt = _timeMapper
                            .GetCorrectedGlookoTime(basalDate)
                            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        Rate = basal.Rate,
                        DataSource = _connectorSource
                    };
                    treatments.Add(treatment);
                }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transforming Glooko batch treatments");
        }

        return treatments;
    }

    public List<Treatment> TransformV3ToTreatments(GlookoV3GraphResponse graphData)
    {
        var treatments = new List<Treatment>();

        if (graphData?.Series == null)
            return treatments;

        var series = graphData.Series;

        var bolusSeries =
            (series.DeliveredBolus ?? Array.Empty<GlookoV3BolusDataPoint>())
            .Concat(series.AutomaticBolus ?? Array.Empty<GlookoV3BolusDataPoint>())
            .Concat(series.InjectionBolus ?? Array.Empty<GlookoV3BolusDataPoint>());

        foreach (var bolus in bolusSeries)
        {
            var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(bolus.X).UtcDateTime;
            var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(bolus.X);
            var carbs = bolus.Data?.CarbsInput ?? 0;
            var insulin = bolus.Data?.DeliveredUnits ?? bolus.Data?.ProgrammedUnits ?? bolus.Y;

            var eventType = _classificationService.ClassifyTreatment(carbs, insulin);

            treatments.Add(
                new Treatment
                {
                    Id = GenerateTreatmentId(
                        eventType,
                        rawTimestamp,
                        $"carbs:{carbs}_insulin:{insulin}"
                    ),
                    EventType = eventType,
                    CreatedAt = correctedTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    Carbs = carbs,
                    Insulin = insulin,
                    DataSource = _connectorSource
                }
            );
        }

        if (series.PumpAlarm != null)
            foreach (var alarm in series.PumpAlarm)
            {
                var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(alarm.X).UtcDateTime;
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(alarm.X);

                var normalizedAlarmType = NormalizeAlarmType(alarm.AlarmType);
                var alarmDescription =
                    alarm.Data?.AlarmDescription
                    ?? alarm.Label
                    ?? alarm.AlarmType
                    ?? "Unknown alarm";

                treatments.Add(
                    new Treatment
                    {
                        Id = GenerateTreatmentId(
                            "Pump Alarm",
                            rawTimestamp,
                            $"type:{normalizedAlarmType}"
                        ),
                        EventType = "Pump Alarm",
                        CreatedAt = correctedTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        Notes = $"[{normalizedAlarmType}] {alarmDescription}",
                        Reason = normalizedAlarmType,
                        DataSource = _connectorSource
                    }
                );

                _logger.LogInformation(
                    "[{ConnectorSource}] Imported pump alarm: type={AlarmType}, description={Description}, timestamp={Timestamp}",
                    _connectorSource,
                    normalizedAlarmType,
                    alarmDescription,
                    correctedTimestamp
                );
            }

        if (series.ReservoirChange != null)
            foreach (var change in series.ReservoirChange)
            {
                var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(change.X).UtcDateTime;
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(change.X);
                treatments.Add(
                    new Treatment
                    {
                        Id = GenerateTreatmentId("Reservoir Change", rawTimestamp),
                        EventType = "Reservoir Change",
                        CreatedAt = correctedTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        Notes = change.Label,
                        DataSource = _connectorSource
                    }
                );
            }

        if (series.SetSiteChange != null)
            foreach (var change in series.SetSiteChange)
            {
                var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(change.X).UtcDateTime;
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(change.X);
                treatments.Add(
                    new Treatment
                    {
                        Id = GenerateTreatmentId("Site Change", rawTimestamp),
                        EventType = "Site Change",
                        CreatedAt = correctedTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        Notes = change.Label,
                        DataSource = _connectorSource
                    }
                );
            }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} treatments from v3 data",
            _connectorSource,
            treatments.Count
        );

        return treatments;
    }

    private string GenerateTreatmentId(
        string eventType,
        DateTime timestamp,
        string? additionalData = null
    )
    {
        var dataToHash = $"glooko_{eventType}_{timestamp.Ticks}_{additionalData ?? ""}";
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(dataToHash));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private string NormalizeAlarmType(string? alarmType)
    {
        if (string.IsNullOrWhiteSpace(alarmType))
            return "Unknown";

        var normalized = alarmType.Trim().ToLowerInvariant();

        return normalized switch
        {
            "occlusion" => "Occlusion",
            "occluded" => "Occlusion",
            "pump occlusion" => "Occlusion",
            "infusion set occlusion" => "Occlusion",
            "cartridge occlusion" => "Occlusion",
            "blockage" => "Occlusion",
            "low battery" => "Low Battery",
            "battery low" => "Low Battery",
            "battery" => "Low Battery",
            "low reservoir" => "Low Reservoir",
            "reservoir low" => "Low Reservoir",
            "empty reservoir" => "Empty Reservoir",
            "infusion set change" => "Infusion Set Change",
            "set change" => "Infusion Set Change",
            "sensor" => "Sensor Error",
            "sensor error" => "Sensor Error",
            "cgm sensor" => "Sensor Error",
            "no delivery" => "No Delivery",
            "delivery error" => "Delivery Error",
            "communication error" => "Communication Error",
            "error" => "Device Error",
            "alarm" => "Device Alarm",
            _ => char.ToUpper(normalized[0]) + normalized.Substring(1)
        };
    }
}