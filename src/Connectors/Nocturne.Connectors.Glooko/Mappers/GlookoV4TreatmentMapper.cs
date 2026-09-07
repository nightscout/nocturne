using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using V4BolusType = Nocturne.Core.Models.V4.BolusType;

namespace Nocturne.Connectors.Glooko.Mappers;

public class GlookoV4TreatmentMapper(string connectorSource, GlookoTimeMapper timeMapper, ILogger logger)
{
    private readonly string _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
    private readonly GlookoTimeMapper _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Maps V2 batch data (NormalBoluses and Foods) to Bolus and CarbIntake records.
    /// </summary>
    public (List<Bolus> boluses, List<CarbIntake> carbs, List<DecompositionBatch> batches) MapBatchData(GlookoBatchData batchData)
    {
        var boluses = new List<Bolus>();
        var carbs = new List<CarbIntake>();
        var batches = new List<DecompositionBatch>();

        try
        {
            if (batchData.NormalBoluses != null)
            {
                foreach (var bolus in batchData.NormalBoluses)
                {
                    try
                    {
                        var bolusDate = _timeMapper.GetRawGlookoDate(bolus.Timestamp, bolus.PumpTimestamp);
                        var correctedDate = _timeMapper.GetCorrectedGlookoTime(bolusDate);
                        var now = DateTime.UtcNow;

                        var hasInsulin = bolus.InsulinDelivered > 0;
                        var hasCarbs = bolus.CarbsInput > 0;

                        Guid? correlationId = null;
                        if (hasInsulin && hasCarbs)
                        {
                            var batch = new DecompositionBatch
                            {
                                Id = Guid.CreateVersion7(),
                                Source = "glooko",
                                SourceRecordId = GenerateLegacyId("bolus", bolusDate, $"insulin:{bolus.InsulinDelivered}_carbs:{bolus.CarbsInput}"),
                                CreatedAt = now,
                            };
                            batches.Add(batch);
                            correlationId = batch.Id;
                        }

                        if (hasInsulin)
                        {
                            var bolusLegacyId = GenerateLegacyId("bolus", bolusDate, $"insulin:{bolus.InsulinDelivered}_carbs:{bolus.CarbsInput}");
                            boluses.Add(new Bolus
                            {
                                Id = Guid.CreateVersion7(),
                                Timestamp = correctedDate,
                                LegacyId = bolusLegacyId,
                                // Stable (raw-timestamp-derived) key so re-correction upserts in place.
                                SyncIdentifier = bolusLegacyId,
                                Device = _connectorSource,
                                DataSource = _connectorSource,
                                Insulin = bolus.InsulinDelivered,
                                BolusType = V4BolusType.Normal,
                                Automatic = false,
                                CorrelationId = correlationId,
                                CreatedAt = now,
                                ModifiedAt = now
                            });
                        }

                        if (hasCarbs)
                        {
                            var carbLegacyId = GenerateLegacyId("carbs", bolusDate, $"insulin:{bolus.InsulinDelivered}_carbs:{bolus.CarbsInput}");
                            carbs.Add(new CarbIntake
                            {
                                Id = Guid.CreateVersion7(),
                                Timestamp = correctedDate,
                                LegacyId = carbLegacyId,
                                SyncIdentifier = carbLegacyId,
                                Device = _connectorSource,
                                DataSource = _connectorSource,
                                Carbs = bolus.CarbsInput,
                                CorrelationId = correlationId,
                                CreatedAt = now,
                                ModifiedAt = now
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping V2 bolus record", _connectorSource);
                    }
                }
            }

            if (batchData.Foods != null)
            {
                foreach (var food in batchData.Foods)
                {
                    try
                    {
                        if (food.SoftDeleted == true) continue;

                        var foodDate = _timeMapper.GetRawGlookoDate(food.Timestamp, food.PumpTimestamp);
                        var correctedDate = _timeMapper.GetCorrectedGlookoTime(foodDate);
                        var carbValue = food.Carbs > 0 ? food.Carbs : food.CarbohydrateGrams;

                        if (carbValue <= 0) continue;

                        var legacyId = !string.IsNullOrEmpty(food.Guid)
                            ? $"glooko_food_{food.Guid}"
                            : GenerateLegacyId("food", foodDate, $"carbs:{carbValue}");

                        var now = DateTime.UtcNow;
                        carbs.Add(new CarbIntake
                        {
                            Id = Guid.CreateVersion7(),
                            Timestamp = correctedDate,
                            LegacyId = legacyId,
                            Device = _connectorSource,
                            DataSource = _connectorSource,
                            Carbs = carbValue,
                            CreatedAt = now,
                            ModifiedAt = now
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping V2 food record", _connectorSource);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ConnectorSource}] Error transforming Glooko V2 batch treatments", _connectorSource);
        }

        return (boluses, carbs, batches);
    }

    /// <summary>
    /// Maps standalone V2 Food records to CarbIntake records.
    /// Used alongside <see cref="MapFoodsToConnectorEntries"/> — this creates the carb event
    /// for the timeline, while the connector entries store the rich nutritional detail.
    /// Filters out soft-deleted items.
    /// </summary>
    public List<CarbIntake> MapFoodsToCarbIntakes(GlookoBatchData batchData)
    {
        var carbs = new List<CarbIntake>();
        if (batchData.Foods == null) return carbs;

        foreach (var food in batchData.Foods)
        {
            try
            {
                if (food.SoftDeleted == true) continue;

                var foodDate = _timeMapper.GetRawGlookoDate(food.Timestamp, food.PumpTimestamp);
                var correctedDate = _timeMapper.GetCorrectedGlookoTime(foodDate);
                var carbValue = food.Carbs > 0 ? food.Carbs : food.CarbohydrateGrams;

                if (carbValue <= 0) continue;

                var legacyId = !string.IsNullOrEmpty(food.Guid)
                    ? $"glooko_food_{food.Guid}"
                    : GenerateLegacyId("food", foodDate, $"carbs:{carbValue}");

                var now = DateTime.UtcNow;
                carbs.Add(new CarbIntake
                {
                    Id = Guid.CreateVersion7(),
                    Timestamp = correctedDate,
                    LegacyId = legacyId,
                    Device = _connectorSource,
                    DataSource = _connectorSource,
                    Carbs = carbValue,
                    CreatedAt = now,
                    ModifiedAt = now
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping V2 food to CarbIntake", _connectorSource);
            }
        }

        return carbs;
    }

    /// <summary>
    /// Maps standalone V2 Food records to connector food entry imports for the food catalog pipeline.
    /// Creates ConnectorFoodEntryImport objects (with Food catalog upsert payloads) that flow through
    /// the existing ConnectorFoodEntryService. Filters out soft-deleted items.
    /// </summary>
    public List<ConnectorFoodEntryImport> MapFoodsToConnectorEntries(GlookoBatchData batchData)
    {
        var results = new List<ConnectorFoodEntryImport>();
        if (batchData.Foods == null) return results;

        foreach (var food in batchData.Foods)
        {
            try
            {
                if (food.SoftDeleted == true) continue;

                var foodDate = _timeMapper.GetRawGlookoDate(food.Timestamp, food.PumpTimestamp);
                var correctedDate = _timeMapper.GetCorrectedGlookoTime(foodDate);
                var carbValue = food.Carbs > 0 ? food.Carbs : food.CarbohydrateGrams;

                if (carbValue <= 0) continue;

                var externalEntryId = !string.IsNullOrEmpty(food.Guid)
                    ? food.Guid
                    : $"glooko_food_{foodDate.Ticks}_{carbValue}";

                var externalFoodId = food.ExternalId ?? food.Guid ?? string.Empty;

                var servingQty = food.ServingQuantity ?? 1;
                var numServings = food.NumberOfServings ?? 1;
                var portions = servingQty * numServings;
                if (portions <= 0) portions = 1;

                results.Add(new ConnectorFoodEntryImport
                {
                    ConnectorSource = _connectorSource,
                    ExternalEntryId = externalEntryId,
                    ExternalFoodId = externalFoodId,
                    ConsumedAt = new DateTimeOffset(correctedDate, TimeSpan.Zero),
                    MealName = food.Description ?? food.Name ?? string.Empty,
                    Carbs = (decimal)carbValue,
                    Protein = (decimal)(food.Protein ?? 0),
                    Fat = (decimal)(food.Fat ?? 0),
                    Energy = (decimal)(food.Calories ?? 0),
                    Servings = (decimal)portions,
                    ServingDescription = BuildServingDescription(food),
                    Food = BuildFoodImport(food, externalFoodId),
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping V2 food record to connector entry", _connectorSource);
            }
        }

        return results;
    }

    /// <summary>
    /// Builds a ConnectorFoodImport for upserting the food catalog record.
    /// </summary>
    private static ConnectorFoodImport? BuildFoodImport(GlookoFood food, string externalFoodId)
    {
        if (string.IsNullOrEmpty(externalFoodId) && string.IsNullOrEmpty(food.Name))
            return null;

        var carbValue = food.Carbs > 0 ? food.Carbs : food.CarbohydrateGrams;

        return new ConnectorFoodImport
        {
            ExternalId = externalFoodId,
            Name = food.Name ?? food.Description ?? string.Empty,
            BrandName = food.Brand,
            Carbs = (decimal)carbValue,
            Protein = (decimal)(food.Protein ?? 0),
            Fat = (decimal)(food.Fat ?? 0),
            Energy = (decimal)(food.Calories ?? 0),
            Portion = (decimal)(food.ServingQuantity ?? 1),
            Unit = food.ServingUnit,
        };
    }

    /// <summary>
    /// Builds a human-readable serving description from Glooko food data.
    /// </summary>
    private static string? BuildServingDescription(GlookoFood food)
    {
        var qty = food.ServingQuantity;
        var unit = food.ServingUnit;
        var numServings = food.NumberOfServings;

        if (qty == null && string.IsNullOrEmpty(unit)) return null;

        var parts = new List<string>();
        if (qty.HasValue) parts.Add(qty.Value.ToString("G"));
        if (!string.IsNullOrEmpty(unit)) parts.Add(unit);
        if (numServings is > 0 and not 1) parts.Add($"x{numServings.Value:G}");

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>
    /// Maps V3 bolus series (DeliveredBolus, AutomaticBolus, InjectionBolus) to Bolus and CarbIntake records.
    /// Manual insulin (gkInsulinBasal, gkInsulinBolus) is handled separately by <see cref="MapV3ManualInsulin"/>.
    /// </summary>
    public (List<Bolus> boluses, List<CarbIntake> carbs, List<DecompositionBatch> batches) MapV3Boluses(GlookoV3GraphResponse graphData)
    {
        var boluses = new List<Bolus>();
        var carbs = new List<CarbIntake>();
        var batches = new List<DecompositionBatch>();

        if (graphData?.Series == null)
            return (boluses, carbs, batches);

        var series = graphData.Series;

        var deliveredBoluses = (series.DeliveredBolus ?? []).Select(b => (bolus: b, automatic: false));
        var automaticBoluses = (series.AutomaticBolus ?? []).Select(b => (bolus: b, automatic: true));
        var injectionBoluses = (series.InjectionBolus ?? []).Select(b => (bolus: b, automatic: false));

        var allBolusSeries = deliveredBoluses.Concat(automaticBoluses).Concat(injectionBoluses);

        foreach (var (bolus, automatic) in allBolusSeries)
        {
            try
            {
                var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(bolus.X).UtcDateTime;
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(bolus.X);
                var now = DateTime.UtcNow;

                var insulin = bolus.Data?.DeliveredUnits ?? bolus.Data?.ProgrammedUnits ?? bolus.Y;
                var carbsInput = bolus.Data?.CarbsInput ?? 0;

                var hasInsulin = insulin > 0;
                var hasCarbs = carbsInput > 0;

                Guid? correlationId = null;
                if (hasInsulin && hasCarbs)
                {
                    var batch = new DecompositionBatch
                    {
                        Id = Guid.CreateVersion7(),
                        Source = "glooko",
                        SourceRecordId = GenerateLegacyId("v3_bolus", rawTimestamp, $"carbs:{carbsInput}_insulin:{insulin}"),
                        CreatedAt = now,
                    };
                    batches.Add(batch);
                    correlationId = batch.Id;
                }

                if (hasInsulin)
                {
                    var v3BolusLegacyId = GenerateLegacyId("v3_bolus", rawTimestamp, $"carbs:{carbsInput}_insulin:{insulin}");
                    boluses.Add(new Bolus
                    {
                        Id = Guid.CreateVersion7(),
                        Timestamp = correctedTimestamp,
                        LegacyId = v3BolusLegacyId,
                        // rawTimestamp is the raw fake-UTC (stable); key the upsert on it so re-correction
                        // moves the row instead of duplicating it.
                        SyncIdentifier = v3BolusLegacyId,
                        Device = _connectorSource,
                        DataSource = _connectorSource,
                        Insulin = insulin,
                        BolusType = V4BolusType.Normal,
                        Automatic = automatic,
                        CorrelationId = correlationId,
                        CreatedAt = now,
                        ModifiedAt = now
                    });
                }

                if (hasCarbs)
                {
                    var v3CarbLegacyId = GenerateLegacyId("v3_carbs", rawTimestamp, $"carbs:{carbsInput}_insulin:{insulin}");
                    carbs.Add(new CarbIntake
                    {
                        Id = Guid.CreateVersion7(),
                        Timestamp = correctedTimestamp,
                        LegacyId = v3CarbLegacyId,
                        SyncIdentifier = v3CarbLegacyId,
                        Device = _connectorSource,
                        DataSource = _connectorSource,
                        Carbs = carbsInput,
                        CorrelationId = correlationId,
                        CreatedAt = now,
                        ModifiedAt = now
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping V3 bolus record at X={X}", _connectorSource, bolus.X);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {BolusCount} boluses and {CarbCount} carb intakes from v3 data",
            _connectorSource, boluses.Count, carbs.Count);

        return (boluses, carbs, batches);
    }

    /// <summary>
    /// Maps V3 carbAll series to CarbIntake records.
    /// These are standalone carb entries from the graph data (meals logged without a bolus wizard,
    /// or quick carb entries). Complements carbs extracted from bolus.Data.CarbsInput in MapV3Boluses.
    /// </summary>
    public List<CarbIntake> MapV3CarbAll(GlookoV3GraphResponse graphData)
    {
        var carbs = new List<CarbIntake>();

        if (graphData?.Series?.CarbAll == null)
            return carbs;

        foreach (var point in graphData.Series.CarbAll)
        {
            try
            {
                var carbValue = point.ActualCarbs ?? 0;
                if (carbValue <= 0) continue;

                var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(point.X).UtcDateTime;
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(point.X);
                var now = DateTime.UtcNow;

                carbs.Add(new CarbIntake
                {
                    Id = Guid.CreateVersion7(),
                    Timestamp = correctedTimestamp,
                    LegacyId = GenerateLegacyId("v3_carball", rawTimestamp, $"carbs:{carbValue}"),
                    Device = _connectorSource,
                    DataSource = _connectorSource,
                    Carbs = carbValue,
                    CreatedAt = now,
                    ModifiedAt = now
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping V3 carbAll record at X={X}",
                    _connectorSource, point.X);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} carb intakes from v3 carbAll series",
            _connectorSource, carbs.Count);

        return carbs;
    }

    /// <summary>
    /// Maps V3 consumable change series (ReservoirChange, SetSiteChange) to DeviceEvent records.
    /// PumpAlarms are skipped here — they are handled by GlookoSystemEventMapper.
    /// </summary>
    public List<DeviceEvent> MapV3DeviceEvents(GlookoV3GraphResponse graphData)
    {
        var deviceEvents = new List<DeviceEvent>();

        if (graphData?.Series == null)
            return deviceEvents;

        var series = graphData.Series;

        if (series.ReservoirChange != null)
        {
            foreach (var change in series.ReservoirChange)
            {
                try
                {
                    var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(change.X).UtcDateTime;
                    var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(change.X);
                    var now = DateTime.UtcNow;

                    deviceEvents.Add(new DeviceEvent
                    {
                        Id = Guid.CreateVersion7(),
                        Timestamp = correctedTimestamp,
                        LegacyId = GenerateLegacyId("reservoir_change", rawTimestamp),
                        Device = _connectorSource,
                        DataSource = _connectorSource,
                        EventType = DeviceEventType.ReservoirChange,
                        Notes = change.Label,
                        CreatedAt = now,
                        ModifiedAt = now
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping V3 reservoir change at X={X}", _connectorSource, change.X);
                }
            }
        }

        if (series.SetSiteChange != null)
        {
            foreach (var change in series.SetSiteChange)
            {
                try
                {
                    var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(change.X).UtcDateTime;
                    var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(change.X);
                    var now = DateTime.UtcNow;

                    deviceEvents.Add(new DeviceEvent
                    {
                        Id = Guid.CreateVersion7(),
                        Timestamp = correctedTimestamp,
                        LegacyId = GenerateLegacyId("site_change", rawTimestamp),
                        Device = _connectorSource,
                        DataSource = _connectorSource,
                        EventType = DeviceEventType.SiteChange,
                        Notes = change.Label,
                        CreatedAt = now,
                        ModifiedAt = now
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping V3 site change at X={X}", _connectorSource, change.X);
                }
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} device events from v3 data",
            _connectorSource, deviceEvents.Count);

        return deviceEvents;
    }

    // ── V3 Histories: Meals → CarbIntake + ConnectorFoodEntryImport ────

    /// <summary>
    /// Maps V3 history meals to CarbIntake records.
    /// Each meal becomes one CarbIntake using the meal-level totals.
    /// Uses the meal guid for stable LegacyId correlation with food entries.
    /// </summary>
    public List<CarbIntake> MapV3HistoryMealsToCarbIntakes(GlookoV3HistoriesResponse historiesData)
    {
        var carbs = new List<CarbIntake>();
        if (historiesData?.Histories == null) return carbs;

        foreach (var meal in ExtractMeals(historiesData))
        {
            try
            {
                if (meal.SoftDeleted == true) continue;

                var totalCarbs = meal.Carbs ?? 0;
                if (totalCarbs <= 0) continue;

                var timestamp = ParseHistoryTimestamp(meal.Timestamp);
                if (timestamp == null) continue;

                var legacyId = !string.IsNullOrEmpty(meal.Guid)
                    ? $"glooko_v3meal_{meal.Guid}"
                    : GenerateLegacyId("v3meal", timestamp.Value, $"carbs:{totalCarbs}");

                var now = DateTime.UtcNow;
                carbs.Add(new CarbIntake
                {
                    Id = Guid.CreateVersion7(),
                    Timestamp = timestamp.Value,
                    LegacyId = legacyId,
                    Device = _connectorSource,
                    DataSource = _connectorSource,
                    Carbs = totalCarbs,
                    CreatedAt = now,
                    ModifiedAt = now
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping V3 history meal", _connectorSource);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} carb intakes from v3 history meals",
            _connectorSource, carbs.Count);

        return carbs;
    }

    /// <summary>
    /// Maps V3 history meals to ConnectorFoodEntryImport records for the food catalog pipeline.
    /// Each food item within a meal becomes a separate ConnectorFoodEntryImport.
    /// The ExternalEntryId uses the food guid for deduplication; the meal guid is preserved
    /// in the MealName field for correlation with the CarbIntake record.
    /// When V2 foods are provided, they are used to enrich the food entries with
    /// externalId (Nutritionix ID) and brand data that V3 histories doesn't include.
    /// </summary>
    public List<ConnectorFoodEntryImport> MapV3HistoryMealsToConnectorEntries(
        GlookoV3HistoriesResponse historiesData,
        GlookoFood[]? v2Foods = null)
    {
        var results = new List<ConnectorFoodEntryImport>();
        if (historiesData?.Histories == null) return results;

        // Build a lookup from V2 food guid → V2 food record for metadata enrichment
        var v2FoodByGuid = new Dictionary<string, GlookoFood>(StringComparer.OrdinalIgnoreCase);
        if (v2Foods != null)
        {
            foreach (var f in v2Foods)
            {
                if (!string.IsNullOrEmpty(f.Guid) && f.SoftDeleted != true)
                    v2FoodByGuid.TryAdd(f.Guid, f);
            }
        }

        foreach (var meal in ExtractMeals(historiesData))
        {
            try
            {
                if (meal.SoftDeleted == true) continue;

                var timestamp = ParseHistoryTimestamp(meal.Timestamp);
                if (timestamp == null) continue;

                if (meal.Foods == null || meal.Foods.Length == 0) continue;

                var mealLabel = meal.MealType ?? "Meal";

                foreach (var food in meal.Foods)
                {
                    try
                    {
                        if (food.SoftDeleted == true) continue;

                        // Try to enrich with V2 food metadata (externalId, brand)
                        var v2Match = food.Guid != null && v2FoodByGuid.TryGetValue(food.Guid, out var v2)
                            ? v2
                            : null;

                        var carbValue = food.Carbs ?? 0;

                        var externalEntryId = food.Guid ?? $"v3meal_{meal.Guid}_{food.Name}";

                        // ExternalFoodId: prefer V2 externalId (Nutritionix ID) for proper deduplication,
                        // fall back to food name when V2 data is unavailable
                        var externalFoodId = v2Match?.ExternalId ?? food.ExternalId ?? food.Name ?? string.Empty;

                        var servingQty = food.ServingQuantity ?? 1;
                        var numServings = food.NumberOfServings ?? 1;
                        var portions = servingQty * numServings;
                        if (portions <= 0) portions = 1;

                        results.Add(new ConnectorFoodEntryImport
                        {
                            ConnectorSource = _connectorSource,
                            ExternalEntryId = externalEntryId,
                            ExternalFoodId = externalFoodId,
                            ConsumedAt = new DateTimeOffset(timestamp.Value, TimeSpan.Zero),
                            MealName = mealLabel,
                            Carbs = (decimal)carbValue,
                            Protein = (decimal)(food.Protein ?? 0),
                            Fat = (decimal)(food.Fat ?? 0),
                            Energy = (decimal)(food.Calories ?? 0),
                            Servings = (decimal)portions,
                            ServingDescription = BuildV3ServingDescription(food),
                            Food = BuildMergedFoodImport(food, v2Match, externalFoodId),
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping V3 history food item '{FoodName}'",
                            _connectorSource, food.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping V3 history meal", _connectorSource);
            }
        }

        var enrichedCount = results.Count(r => !string.IsNullOrEmpty(r.Food?.BrandName) || r.ExternalFoodId != r.Food?.Name);
        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} food entries from v3 history meals ({Enriched} enriched with v2 metadata)",
            _connectorSource, results.Count, enrichedCount);

        return results;
    }

    /// <summary>
    /// Builds a ConnectorFoodImport for the food catalog, merging V3 history food data
    /// with V2 food metadata (externalId, brand) when available.
    /// </summary>
    private static ConnectorFoodImport? BuildMergedFoodImport(
        GlookoV3HistoryFood food, GlookoFood? v2Match, string externalFoodId)
    {
        if (string.IsNullOrEmpty(food.Name))
            return null;

        return new ConnectorFoodImport
        {
            ExternalId = externalFoodId,
            Name = food.Name,
            BrandName = v2Match?.Brand ?? food.Brand,
            Carbs = (decimal)(food.Carbs ?? 0),
            Protein = (decimal)(food.Protein ?? 0),
            Fat = (decimal)(food.Fat ?? 0),
            Energy = (decimal)(food.Calories ?? 0),
            Portion = (decimal)(food.ServingQuantity ?? 1),
            Unit = food.ServingUnit,
        };
    }

    /// <summary>
    /// Builds a human-readable serving description from V3 history food data.
    /// </summary>
    private static string? BuildV3ServingDescription(GlookoV3HistoryFood food)
    {
        var qty = food.ServingQuantity;
        var unit = food.ServingUnit;
        var numServings = food.NumberOfServings;

        if (qty == null && string.IsNullOrEmpty(unit)) return null;

        var parts = new List<string>();
        if (qty.HasValue) parts.Add(qty.Value.ToString("G"));
        if (!string.IsNullOrEmpty(unit)) parts.Add(unit);
        if (numServings is > 0 and not 1) parts.Add($"x{numServings.Value:G}");

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>
    /// Parses a V3 histories timestamp string and applies timezone correction.
    /// V3 histories timestamps follow the same pattern as V2: labeled as UTC but actually local time.
    /// </summary>
    private DateTime? ParseHistoryTimestamp(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp)) return null;

        if (!DateTime.TryParse(timestamp, out var parsedDate))
        {
            logger.LogWarning("Failed to parse V3 history timestamp: '{Timestamp}'", timestamp);
            return null;
        }

        // V3 histories timestamps are in the same fake-UTC format as V2
        return _timeMapper.GetCorrectedGlookoTime(parsedDate.ToUniversalTime());
    }

    /// <summary>
    /// Extracts meal items from the flat V3 histories list.
    /// The histories array contains typed entries; this filters for type == "meals"
    /// and returns the meal data from each entry's Item property.
    /// </summary>
    public static IEnumerable<GlookoV3HistoryMeal> ExtractMeals(GlookoV3HistoriesResponse historiesData)
    {
        if (historiesData?.Histories == null) yield break;

        foreach (var entry in historiesData.Histories)
        {
            if (entry.Type == "meals" && entry.Item != null && entry.SoftDeleted != true)
                yield return entry.Item;
        }
    }

    /// <summary>
    /// Maps V3 manual insulin series (gkInsulinBasal → BasalInjection, gkInsulinBolus → Bolus).
    /// These represent pen injections logged by the user, not pump-delivered insulin.
    /// Insulin names from Glooko (e.g., "Tresiba®U100", "Admelog®") are matched against
    /// the <see cref="InsulinCatalog"/> to populate <see cref="TreatmentInsulinContext"/>.
    /// </summary>
    public (List<BasalInjection> basalInjections, List<Bolus> boluses) MapV3ManualInsulin(GlookoV3GraphResponse graphData)
    {
        var basalInjections = new List<BasalInjection>();
        var boluses = new List<Bolus>();

        if (graphData?.Series == null)
            return (basalInjections, boluses);

        // Map gkInsulinBasal → BasalInjection
        if (graphData.Series.GkInsulinBasal != null)
        {
            foreach (var point in graphData.Series.GkInsulinBasal)
            {
                try
                {
                    var units = point.ActualUnits;
                    if (units <= 0) continue;

                    var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(point.X).UtcDateTime;
                    var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(point.X);
                    var now = DateTime.UtcNow;

                    var insulinContext = ResolveInsulinContext(point.InsulinName, InsulinCategory.LongActing, InsulinCategory.UltraLongActing);

                    basalInjections.Add(new BasalInjection
                    {
                        Id = Guid.CreateVersion7(),
                        Timestamp = correctedTimestamp,
                        LegacyId = GenerateLegacyId("v3_insulin_basal", rawTimestamp, $"units:{units}_name:{point.InsulinName}"),
                        Device = _connectorSource,
                        DataSource = _connectorSource,
                        Units = units,
                        InsulinContext = insulinContext,
                        CreatedAt = now,
                        ModifiedAt = now
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping V3 gkInsulinBasal at X={X}", _connectorSource, point.X);
                }
            }
        }

        // Map gkInsulinBolus → Bolus (pen bolus injection)
        if (graphData.Series.GkInsulinBolus != null)
        {
            foreach (var point in graphData.Series.GkInsulinBolus)
            {
                try
                {
                    var units = point.ActualUnits;
                    if (units <= 0) continue;

                    var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(point.X).UtcDateTime;
                    var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(point.X);
                    var now = DateTime.UtcNow;

                    var insulinContext = ResolveInsulinContext(point.InsulinName, InsulinCategory.RapidActing, InsulinCategory.ShortActing);

                    boluses.Add(new Bolus
                    {
                        Id = Guid.CreateVersion7(),
                        Timestamp = correctedTimestamp,
                        LegacyId = GenerateLegacyId("v3_insulin_bolus", rawTimestamp, $"units:{units}_name:{point.InsulinName}"),
                        Device = _connectorSource,
                        DataSource = _connectorSource,
                        Insulin = units,
                        BolusType = V4BolusType.Normal,
                        Automatic = false,
                        InsulinType = point.InsulinName,
                        InsulinContext = insulinContext,
                        CreatedAt = now,
                        ModifiedAt = now
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping V3 gkInsulinBolus at X={X}", _connectorSource, point.X);
                }
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {BasalCount} basal injections and {BolusCount} pen boluses from v3 manual insulin data",
            _connectorSource, basalInjections.Count, boluses.Count);

        return (basalInjections, boluses);
    }

    /// <summary>
    /// Maps the SSV2 pen-injection feeds (injection_basals → BasalInjection, injection_boluses → Bolus) —
    /// the SSV2 counterpart to <see cref="MapV3ManualInsulin"/>. Records are keyed on their stable Glooko
    /// guid (falling back to the raw fake-UTC timestamp) so re-correction upserts in place rather than
    /// duplicating. Insulin names (e.g. "Tresiba®U100") are matched against the <see cref="InsulinCatalog"/>
    /// for DIA/peak, defaulting by category when unknown.
    /// </summary>
    public (List<BasalInjection> basalInjections, List<Bolus> boluses) MapSsv2InjectionInsulin(
        IReadOnlyList<GlookoInjectionInsulin> injectionBasals,
        IReadOnlyList<GlookoInjectionInsulin> injectionBoluses)
    {
        var basalInjections = new List<BasalInjection>();
        var boluses = new List<Bolus>();

        foreach (var basal in injectionBasals)
        {
            try
            {
                if (basal.SoftDeleted || basal.InsulinDelivered <= 0) continue;

                var rawTimestamp = _timeMapper.GetRawGlookoDate(basal.Timestamp, basal.PumpTimestamp);
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);
                var now = DateTime.UtcNow;

                var legacyId = !string.IsNullOrEmpty(basal.Guid)
                    ? $"glooko_injection_basal_{basal.Guid}"
                    : GenerateLegacyId("ssv2_injection_basal", rawTimestamp, $"units:{basal.InsulinDelivered}_name:{basal.Name}");

                basalInjections.Add(new BasalInjection
                {
                    Id = Guid.CreateVersion7(),
                    Timestamp = correctedTimestamp,
                    LegacyId = legacyId,
                    SyncIdentifier = legacyId,
                    Device = _connectorSource,
                    DataSource = _connectorSource,
                    Units = basal.InsulinDelivered,
                    InsulinContext = ResolveInsulinContext(basal.Name, InsulinCategory.LongActing, InsulinCategory.UltraLongActing),
                    CreatedAt = now,
                    ModifiedAt = now
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping SSV2 injection basal", _connectorSource);
            }
        }

        foreach (var bolus in injectionBoluses)
        {
            try
            {
                if (bolus.SoftDeleted || bolus.InsulinDelivered <= 0) continue;

                var rawTimestamp = _timeMapper.GetRawGlookoDate(bolus.Timestamp, bolus.PumpTimestamp);
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);
                var now = DateTime.UtcNow;

                var legacyId = !string.IsNullOrEmpty(bolus.Guid)
                    ? $"glooko_injection_bolus_{bolus.Guid}"
                    : GenerateLegacyId("ssv2_injection_bolus", rawTimestamp, $"units:{bolus.InsulinDelivered}_name:{bolus.Name}");

                boluses.Add(new Bolus
                {
                    Id = Guid.CreateVersion7(),
                    Timestamp = correctedTimestamp,
                    LegacyId = legacyId,
                    SyncIdentifier = legacyId,
                    Device = _connectorSource,
                    DataSource = _connectorSource,
                    Insulin = bolus.InsulinDelivered,
                    BolusType = V4BolusType.Normal,
                    Automatic = false,
                    InsulinType = bolus.Name,
                    InsulinContext = ResolveInsulinContext(bolus.Name, InsulinCategory.RapidActing, InsulinCategory.ShortActing),
                    CreatedAt = now,
                    ModifiedAt = now
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping SSV2 injection bolus", _connectorSource);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {BasalCount} basal injections and {BolusCount} pen boluses from SSV2 injection feeds",
            _connectorSource, basalInjections.Count, boluses.Count);

        return (basalInjections, boluses);
    }

    /// <summary>
    /// Maps the SSV2 <c>cgm/insulin_events</c> feed — app-logged insulin doses for CGM-only/MDI users who
    /// log doses in the app rather than via a pump. <c>insulin_type</c> selects the target: "fast_acting"/
    /// "rapid" → rapid <see cref="Bolus"/>; "long_acting"/"intermediate"/"basal" → long-acting
    /// <see cref="BasalInjection"/>; an unknown/missing type defaults to Bolus. The DIA/peak context is
    /// resolved by category (no product name is supplied by this feed). Uses <c>display_time</c> (falling
    /// back to <c>event_time</c>), keyed on the stable Glooko guid (raw-timestamp hash fallback);
    /// soft-delete aware and skips non-positive doses.
    /// </summary>
    public (List<BasalInjection> basalInjections, List<Bolus> boluses) MapSsv2InsulinEvents(
        IReadOnlyList<GlookoSsv2InsulinEvent> insulinEvents)
    {
        var basalInjections = new List<BasalInjection>();
        var boluses = new List<Bolus>();

        foreach (var evt in insulinEvents)
        {
            try
            {
                if (evt.SoftDeleted || evt.Insulin <= 0) continue;

                // display_time wins over event_time: GetRawGlookoDate prefers its second arg when present.
                var rawTimestamp = _timeMapper.GetRawGlookoDate(evt.EventTime ?? string.Empty, evt.DisplayTime);
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);
                var now = DateTime.UtcNow;

                var isBasal = IsLongActingInsulinType(evt.InsulinType);

                if (isBasal)
                {
                    var legacyId = !string.IsNullOrEmpty(evt.Guid)
                        ? $"glooko_insulin_event_basal_{evt.Guid}"
                        : GenerateLegacyId("ssv2_insulin_event_basal", rawTimestamp, $"units:{evt.Insulin}");

                    basalInjections.Add(new BasalInjection
                    {
                        Id = Guid.CreateVersion7(),
                        Timestamp = correctedTimestamp,
                        LegacyId = legacyId,
                        SyncIdentifier = legacyId,
                        Device = _connectorSource,
                        DataSource = _connectorSource,
                        Units = evt.Insulin,
                        InsulinContext = ResolveInsulinContext(null, InsulinCategory.LongActing, InsulinCategory.UltraLongActing),
                        CreatedAt = now,
                        ModifiedAt = now
                    });
                }
                else
                {
                    var legacyId = !string.IsNullOrEmpty(evt.Guid)
                        ? $"glooko_insulin_event_bolus_{evt.Guid}"
                        : GenerateLegacyId("ssv2_insulin_event_bolus", rawTimestamp, $"units:{evt.Insulin}");

                    boluses.Add(new Bolus
                    {
                        Id = Guid.CreateVersion7(),
                        Timestamp = correctedTimestamp,
                        LegacyId = legacyId,
                        SyncIdentifier = legacyId,
                        Device = _connectorSource,
                        DataSource = _connectorSource,
                        Insulin = evt.Insulin,
                        BolusType = V4BolusType.Normal,
                        Automatic = false,
                        InsulinContext = ResolveInsulinContext(null, InsulinCategory.RapidActing, InsulinCategory.ShortActing),
                        CreatedAt = now,
                        ModifiedAt = now
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping SSV2 insulin event", _connectorSource);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {BasalCount} basal injections and {BolusCount} boluses from SSV2 insulin events",
            _connectorSource, basalInjections.Count, boluses.Count);

        return (basalInjections, boluses);
    }

    /// <summary>
    /// Classifies a Glooko <c>insulin_type</c> as long-acting (→ BasalInjection) vs rapid (→ Bolus).
    /// "long_acting"/"intermediate"/"basal" → true; "fast_acting"/"rapid" and any unknown/missing value
    /// → false (default to Bolus, the safer assumption for app-logged MDI doses).
    /// </summary>
    private static bool IsLongActingInsulinType(string? insulinType) =>
        (insulinType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "long_acting" or "intermediate" or "basal" => true,
            _ => false,
        };

    /// <summary>
    /// Maps the SSV2 <c>cgm/carbs_events</c> feed (standalone app-logged carbs, not attached to a bolus)
    /// to <see cref="CarbIntake"/> records — the SSV2 counterpart to the v3 graph's <c>carbAll</c> series.
    /// Keyed on the stable Glooko guid (raw-timestamp hash fallback); skips soft-deleted and non-positive
    /// entries.
    /// </summary>
    public List<CarbIntake> MapSsv2CarbsEvents(IReadOnlyList<GlookoSsv2CarbsEvent> carbsEvents)
    {
        var carbs = new List<CarbIntake>();

        foreach (var evt in carbsEvents)
        {
            try
            {
                if (evt.SoftDeleted || evt.CgmCarbs <= 0) continue;

                // GetRawGlookoDate prefers its 2nd arg, so pass DisplayTime there to prefer it,
                // falling back to EventTime then the legacy timestamp field.
                var rawTimestamp = _timeMapper.GetRawGlookoDate(
                    evt.EventTime ?? evt.Timestamp ?? string.Empty, evt.DisplayTime);
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);
                var now = DateTime.UtcNow;

                var legacyId = !string.IsNullOrEmpty(evt.Guid)
                    ? $"glooko_carbs_event_{evt.Guid}"
                    : GenerateLegacyId("ssv2_carbs_event", rawTimestamp, $"carbs:{evt.CgmCarbs}");

                carbs.Add(new CarbIntake
                {
                    Id = Guid.CreateVersion7(),
                    Timestamp = correctedTimestamp,
                    LegacyId = legacyId,
                    SyncIdentifier = legacyId,
                    Device = _connectorSource,
                    DataSource = _connectorSource,
                    Carbs = evt.CgmCarbs,
                    CreatedAt = now,
                    ModifiedAt = now
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping SSV2 carbs event", _connectorSource);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} carb intakes from SSV2 carbs events", _connectorSource, carbs.Count);

        return carbs;
    }

    /// <summary>
    /// Maps the SSV2 <c>pumps/extended_boluses</c> feed to <see cref="Bolus"/> records. An extended bolus
    /// has an immediate portion (<c>initialDelivery</c>) plus a portion delivered over a duration
    /// (<c>extendedDelivery</c> across <c>extendedBolusDuration</c>): both present → Dual, otherwise Square.
    /// Net-new — the v3 graph has no extended-bolus series. Keyed on the stable Glooko guid (raw-timestamp
    /// hash fallback); soft-delete aware. (Carbs on an extended meal bolus are not decomposed here yet.)
    /// </summary>
    public List<Bolus> MapSsv2ExtendedBoluses(IReadOnlyList<GlookoExtendedBolus> extendedBoluses)
    {
        var boluses = new List<Bolus>();

        foreach (var eb in extendedBoluses)
        {
            try
            {
                if (eb.SoftDeleted) continue;

                var initial = eb.InitialDelivery ?? 0;
                var extended = eb.ExtendedDelivery ?? 0;
                var total = eb.InsulinDelivered > 0 ? eb.InsulinDelivered : initial + extended;
                if (total <= 0) continue;

                var rawTimestamp = _timeMapper.GetRawGlookoDate(eb.Timestamp, eb.PumpTimestamp);
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);
                var now = DateTime.UtcNow;

                var bolusType = initial > 0 && extended > 0 ? V4BolusType.Dual : V4BolusType.Square;

                var legacyId = !string.IsNullOrEmpty(eb.Guid)
                    ? $"glooko_extended_bolus_{eb.Guid}"
                    : GenerateLegacyId("ssv2_extended_bolus", rawTimestamp, $"insulin:{total}");

                boluses.Add(new Bolus
                {
                    Id = Guid.CreateVersion7(),
                    Timestamp = correctedTimestamp,
                    LegacyId = legacyId,
                    SyncIdentifier = legacyId,
                    Device = _connectorSource,
                    DataSource = _connectorSource,
                    Insulin = total,
                    BolusType = bolusType,
                    Duration = eb.ExtendedBolusDuration > 0 ? eb.ExtendedBolusDuration : null,
                    Automatic = false,
                    CreatedAt = now,
                    ModifiedAt = now
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping SSV2 extended bolus", _connectorSource);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} extended boluses from SSV2 data", _connectorSource, boluses.Count);

        return boluses;
    }

    /// <summary>
    /// Resolves a Glooko insulin name (e.g., "Tresiba®U100", "Admelog®") to a
    /// <see cref="TreatmentInsulinContext"/> by fuzzy-matching against the <see cref="InsulinCatalog"/>.
    /// Falls back to a generic context with the raw name if no match is found.
    /// </summary>
    private static TreatmentInsulinContext ResolveInsulinContext(
        string? glookoName,
        InsulinCategory primaryCategory,
        InsulinCategory secondaryCategory)
    {
        if (string.IsNullOrWhiteSpace(glookoName))
        {
            // No name provided — use a generic entry from the primary category
            var fallback = InsulinCatalog.GetByCategory(primaryCategory).FirstOrDefault()
                ?? InsulinCatalog.GetByCategory(secondaryCategory).FirstOrDefault();
            return new TreatmentInsulinContext
            {
                PatientInsulinId = Guid.Empty,
                InsulinName = "Unknown",
                Dia = fallback?.DefaultDia ?? 4.0,
                Peak = fallback?.DefaultPeak ?? 75,
                Curve = fallback?.Curve ?? "bilinear",
                Concentration = fallback?.Concentration ?? 100,
            };
        }

        // Normalize: strip ® and trademark symbols, lowercase for matching
        var normalized = glookoName
            .Replace("®", "", StringComparison.Ordinal)
            .Replace("™", "", StringComparison.Ordinal)
            .Trim();

        // Hyphen-free version for ID matching: catalog IDs use hyphens (e.g., "humalog-u200")
        // but Glooko names concatenate them ("HumalogU200") or use spaces ("Humulin R U500")
        var normalizedCompact = normalized
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);

        // Try catalog ID match, most-specific (longest ID) first so that
        // "HumalogU200" matches "humalog-u200" before the shorter "humalog".
        var match = InsulinCatalog.GetAll()
            .OrderByDescending(f => f.Id.Length)
            .FirstOrDefault(f =>
                normalizedCompact.Contains(f.Id.Replace("-", ""), StringComparison.OrdinalIgnoreCase));

        // Try matching by catalog name keywords
        if (match == null)
        {
            // Extract the first word as the brand name for matching
            var brandWord = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            match = InsulinCatalog.GetAll()
                .OrderByDescending(f => f.Id.Length)
                .FirstOrDefault(f => f.Name.Contains(brandWord, StringComparison.OrdinalIgnoreCase));
        }

        if (match != null)
        {
            return new TreatmentInsulinContext
            {
                PatientInsulinId = Guid.Empty,
                InsulinName = match.Name,
                Dia = match.DefaultDia,
                Peak = match.DefaultPeak,
                Curve = match.Curve,
                Concentration = match.Concentration,
            };
        }

        // No match — use the raw Glooko name with defaults from the primary category
        var categoryDefault = InsulinCatalog.GetByCategory(primaryCategory).FirstOrDefault()
            ?? InsulinCatalog.GetByCategory(secondaryCategory).FirstOrDefault();
        return new TreatmentInsulinContext
        {
            PatientInsulinId = Guid.Empty,
            InsulinName = glookoName,
            Dia = categoryDefault?.DefaultDia ?? 4.0,
            Peak = categoryDefault?.DefaultPeak ?? 75,
            Curve = categoryDefault?.Curve ?? "bilinear",
            Concentration = categoryDefault?.Concentration ?? 100,
        };
    }

    private static string GenerateLegacyId(string eventType, DateTime timestamp, string? additionalData = null)
    {
        var dataToHash = $"glooko_{eventType}_{timestamp.Ticks}_{additionalData ?? string.Empty}";
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(dataToHash));
        return $"glooko_{Convert.ToHexString(hashBytes).ToLowerInvariant()}";
    }
}
