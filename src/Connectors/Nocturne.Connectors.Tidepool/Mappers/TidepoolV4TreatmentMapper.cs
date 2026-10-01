using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tidepool.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Tidepool.Mappers;

public class TidepoolV4TreatmentMapper(ILogger logger, string connectorSource)
{
    private readonly string _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public (List<Bolus> Boluses, List<CarbIntake> CarbIntakes, List<DecompositionBatch> Batches) MapTreatments(
        TidepoolBolus[]? boluses,
        TidepoolFood[]? foods)
    {
        var mappedBoluses = new List<(TidepoolBolus Source, Bolus Mapped)>();
        var mappedCarbs = new List<CarbIntake>();
        var batches = new List<DecompositionBatch>();

        if (boluses != null)
        {
            foreach (var bolus in boluses.Where(b => b.Time.HasValue))
            {
                try
                {
                    var mapped = MapBolus(bolus);
                    if (mapped != null)
                        mappedBoluses.Add((bolus, mapped));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error mapping Tidepool bolus: {Id}", bolus.Id);
                }
            }
        }

        var correlationBolusByTime = mappedBoluses
            .GroupBy(b => b.Source.Time!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(b => b.Mapped.Insulin)
                    .ThenBy(b => b.Source.Id, StringComparer.Ordinal)
                    .First().Mapped);

        // Process foods: correlate with bolus at same timestamp if present
        if (foods != null)
        {
            foreach (var food in foods.Where(f => f.Time.HasValue))
            {
                try
                {
                    var carbs = food.Nutrition?.Carbohydrate?.Net;
                    if (!carbs.HasValue || carbs.Value <= 0) continue;

                    var timestamp = food.Time!.Value.ToUniversalTime();
                    var now = DateTime.UtcNow;

                    if (correlationBolusByTime.TryGetValue(food.Time!.Value, out var existingBolus))
                    {
                        // Correlate bolus and carb at the same timestamp
                        var batch = new DecompositionBatch
                        {
                            Id = Guid.CreateVersion7(),
                            Source = "tidepool",
                            SourceRecordId = food.Id,
                            CreatedAt = now,
                        };
                        batches.Add(batch);
                        existingBolus.CorrelationId = batch.Id;

                        mappedCarbs.Add(new CarbIntake
                        {
                            Id = Guid.CreateVersion7(),
                            Timestamp = timestamp,
                            LegacyId = $"tidepool_{food.Id}",
                            Device = _connectorSource,
                            DataSource = _connectorSource,
                            Carbs = carbs.Value,
                            CorrelationId = batch.Id,
                            CreatedAt = now,
                            ModifiedAt = now
                        });
                    }
                    else
                    {
                        // Standalone carb entry
                        mappedCarbs.Add(new CarbIntake
                        {
                            Id = Guid.CreateVersion7(),
                            Timestamp = timestamp,
                            LegacyId = $"tidepool_{food.Id}",
                            Device = _connectorSource,
                            DataSource = _connectorSource,
                            Carbs = carbs.Value,
                            CreatedAt = now,
                            ModifiedAt = now
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error mapping Tidepool food: {Id}", food.Id);
                }
            }
        }

        return (mappedBoluses.Select(b => b.Mapped).ToList(), mappedCarbs, batches);
    }

    private Bolus? MapBolus(TidepoolBolus bolus)
    {
        if (!bolus.Time.HasValue) return null;

        var totalInsulin = (bolus.Normal ?? 0) + (bolus.Extended ?? 0);
        if (totalInsulin <= 0) return null;

        var timestamp = bolus.Time!.Value.ToUniversalTime();
        var now = DateTime.UtcNow;

        BolusType bolusType;
        if (string.Equals(bolus.SubType, "dual/square", StringComparison.OrdinalIgnoreCase)
            || (bolus.Normal.HasValue && bolus.Normal > 0 && bolus.Extended.HasValue && bolus.Extended > 0))
        {
            bolusType = BolusType.Dual;
        }
        else if (string.Equals(bolus.SubType, "square", StringComparison.OrdinalIgnoreCase)
            || (bolus.Extended.HasValue && bolus.Extended > 0))
        {
            bolusType = BolusType.Square;
        }
        else
        {
            bolusType = BolusType.Normal;
        }

        double? durationMinutes = bolus.Duration.HasValue
            ? bolus.Duration.Value.TotalMinutes
            : null;

        return new Bolus
        {
            Id = Guid.CreateVersion7(),
            Timestamp = timestamp,
            LegacyId = $"tidepool_{bolus.Id}",
            Device = _connectorSource,
            DataSource = _connectorSource,
            Insulin = totalInsulin,
            BolusType = bolusType,
            Automatic = false,
            Duration = durationMinutes,
            CreatedAt = now,
            ModifiedAt = now
        };
    }
}
