using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// The stored keys of every Glooko record type, as exact strings: the keyed shapes and the hashed shapes used when Glooko sends no guid.
/// Why the keys must not change: <see cref="Nocturne.Connectors.Core.Utilities.ConnectorLegacyIds"/>.
/// </summary>
public class GlookoLegacyIdTests
{
    private const string Source = "glooko-connector";
    private const string V2Time = "2026-04-28T10:00:00Z";
    private const long V3X = 1_779_230_416;

    private static GlookoTimeMapper Time() => new(new GlookoConnectorConfiguration(), NullLogger.Instance);

    private static GlookoSensorGlucoseMapper SensorGlucose() =>
        new(new GlookoConnectorConfiguration(), Source, Time(), NullLogger.Instance);

    private static GlookoV4TreatmentMapper Treatments() => new(Source, Time(), NullLogger.Instance);

    private static GlookoTempBasalMapper TempBasals() => new(Source, Time(), NullLogger.Instance);

    private static GlookoStateSpanMapper StateSpans() => new(Source, Time(), NullLogger.Instance);

    private static GlookoSystemEventMapper SystemEvents() => new(Source, Time(), NullLogger.Instance);

    private static GlookoPumpEventMapper PumpEvents() => new(Source, Time(), NullLogger.Instance);

    private static GlookoNoteMapper Notes() => new(Source, Time(), NullLogger.Instance);

    private static GlookoActivityMapper Activities() => new(Source, Time(), NullLogger.Instance);

    private static GlookoV3GraphResponse Graph(GlookoV3Series series) => new() { Series = series };

    [Fact]
    public void V2Readings()
    {
        var batch = new GlookoBatchData
        {
            Readings =
            [
                new GlookoCgmReading { Timestamp = V2Time, Value = 12000, Guid = "cgm-1" },
                new GlookoCgmReading { Timestamp = "2026-04-28T10:05:00Z", Value = 12500 },
            ],
            MeterReadings =
            [
                new GlookoMeterReading { Timestamp = V2Time, Value = 11000, Guid = "meter-1" },
                new GlookoMeterReading { Timestamp = "2026-04-28T10:05:00Z", Value = 11500 },
            ],
        };

        SensorGlucose().TransformBatchDataToSensorGlucose(batch).Select(r => r.LegacyId)
            .Should().Equal("glooko_cgm_cgm-1", "glooko_639129675000000000");
        SensorGlucose().TransformBatchDataToBGChecks(batch).Select(r => r.LegacyId)
            .Should().Equal("glooko_meter_meter-1", "glooko_meter_639129675000000000");
    }

    [Fact]
    public void Egvs()
    {
        GlookoEgv[] egvs =
        [
            new() { DisplayTime = V2Time, GlucoseValue = 12000, Guid = "egv-1" },
            new() { DisplayTime = "2026-04-28T10:05:00Z", GlucoseValue = 12500 },
        ];

        SensorGlucose().TransformEgvsToSensorGlucose(egvs).Select(r => r.LegacyId)
            .Should().Equal("glooko_egv_egv-1", "glooko_egv_raw_2026-04-28T10:05:00Z");
    }

    [Fact]
    public void V3Readings()
    {
        var graph = Graph(new GlookoV3Series
        {
            CgmNormal = [new GlookoV3GlucoseDataPoint { X = V3X, Y = 120 }],
            BgNormal =
            [
                new GlookoV3GlucoseDataPoint { X = V3X, Value = 11000, Id = "bg-1" },
                new GlookoV3GlucoseDataPoint { X = V3X + 60, Value = 11500 },
            ],
        });

        SensorGlucose().TransformV3ToSensorGlucose(graph, null).Single().LegacyId.Should().Be("glooko_v3_1779230416");
        SensorGlucose().TransformV3ToBGChecks(graph, null).Select(r => r.LegacyId)
            .Should().Equal("glooko_v3_bg_bg-1", "glooko_v3_bg_1779230476");
    }

    [Fact]
    public void V2BolusesAndFoods()
    {
        var batch = new GlookoBatchData
        {
            NormalBoluses = [new GlookoBolus { Timestamp = V2Time, PumpTimestamp = V2Time, InsulinDelivered = 3.5, CarbsInput = 40 }],
            Foods =
            [
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 20, Guid = "food-1" },
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 25.5 },
            ],
        };

        var (boluses, carbs, _) = Treatments().MapBatchData(batch);

        boluses.Single().LegacyId.Should().Be("glooko_e4ca2d1ff437616a8d034817cd8c51e40c18f0b3");
        carbs.Select(c => c.LegacyId).Should().Equal(
            "glooko_4cbbee06f06b7d61452d21647171407474e106bf",
            "glooko_food_food-1",
            "glooko_4a9ba130561a4e74092a6432e1950b7f283ba7e7");
        Treatments().MapBatchData(new GlookoBatchData { Foods = batch.Foods }).carbs.Select(c => c.LegacyId)
            .Should().Equal("glooko_food_food-1", "glooko_4a9ba130561a4e74092a6432e1950b7f283ba7e7");
    }

    [Fact]
    public void V2FoodEntriesNameTheLegacyIdTheirCarbIntakeIsStoredUnder()
    {
        var batch = new GlookoBatchData
        {
            Foods =
            [
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 20, Guid = "food-1" },
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 25.5 },
            ],
        };

        var entries = Treatments().MapFoodsToConnectorEntries(batch);

        entries.Select(e => e.CarbIntakeLegacyId).Should().Equal(
            "glooko_food_food-1", "glooko_4a9ba130561a4e74092a6432e1950b7f283ba7e7");
        entries.Select(e => e.CarbIntakeLegacyId).Should().Equal(
            Treatments().MapBatchData(batch).carbs.Select(c => c.LegacyId));
    }

    [Fact]
    public void V2FirstGuidlessFoodKeepsItsStoredIdsAndTheNextOnesAreToldApart()
    {
        var batch = new GlookoBatchData
        {
            Foods =
            [
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 25.5 },
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 25.5 },
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 25.5 },
            ],
        };

        var (_, carbs, _) = Treatments().MapBatchData(batch);
        var entries = Treatments().MapFoodsToConnectorEntries(batch);

        carbs.Select(c => c.LegacyId).First().Should().Be("glooko_4a9ba130561a4e74092a6432e1950b7f283ba7e7");
        entries.Select(e => e.Entry.ExternalEntryId).First().Should().Be("glooko_food_639129672000000000_25.5");
        entries.Select(e => e.Entry.ExternalEntryId).Skip(1).Should().Equal(
            "glooko_food_639129672000000000_25.5_occurrence:1", "glooko_food_639129672000000000_25.5_occurrence:2");
        carbs.Select(c => c.LegacyId).Should().OnlyHaveUniqueItems();
        entries.Select(e => e.Entry.ExternalEntryId).Should().OnlyHaveUniqueItems();
        entries.Select(e => e.CarbIntakeLegacyId).Should().Equal(carbs.Select(c => c.LegacyId));
    }

    [Fact]
    public void V2TwoGuidlessFoodsAtOneTimeWithEqualCarbsAreTwoCarbIntakesAndTwoFoodEntries()
    {
        var batch = new GlookoBatchData
        {
            Foods =
            [
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 20, Description = "Apple" },
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 20, Description = "Bread" },
            ],
        };

        var (_, carbs, _) = Treatments().MapBatchData(batch);
        var entries = Treatments().MapFoodsToConnectorEntries(batch);

        carbs.Should().HaveCount(2);
        carbs.Sum(c => c.Carbs).Should().Be(40);
        carbs.Select(c => c.LegacyId).Should().OnlyHaveUniqueItems();
        entries.Select(e => e.Entry.MealName).Should().Equal("Apple", "Bread");
        entries.Select(e => e.Entry.ExternalEntryId).Should().OnlyHaveUniqueItems();
        entries.Select(e => e.CarbIntakeLegacyId).Should().Equal(carbs.Select(c => c.LegacyId));
    }

    [Fact]
    public void V2FoodsWithAGuidOrADifferentTimeOrCarbsDoNotCountAsRepeats()
    {
        var batch = new GlookoBatchData
        {
            Foods =
            [
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 20, Guid = "food-1" },
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 20, Guid = "food-2" },
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 20 },
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 21 },
                new GlookoFood { Timestamp = "2026-04-28T10:05:00Z", PumpTimestamp = "2026-04-28T10:05:00Z", Carbs = 20 },
            ],
        };

        var entries = Treatments().MapFoodsToConnectorEntries(batch);

        entries.Select(e => e.Entry.ExternalEntryId).Should().Equal(
            "food-1", "food-2", "glooko_food_639129672000000000_20", "glooko_food_639129672000000000_21",
            "glooko_food_639129675000000000_20");
        entries.Select(e => e.CarbIntakeLegacyId).Take(2).Should().Equal("glooko_food_food-1", "glooko_food_food-2");
    }

    [Fact]
    public void V2SkippedTwinsDoNotMoveTheIdsOfTheLiveOnes()
    {
        var batch = new GlookoBatchData
        {
            Foods =
            [
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 20, Description = "A", SoftDeleted = true },
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 20, Description = "B" },
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 0, Description = "C" },
                new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 20, Description = "D" },
            ],
        };

        var (_, carbs, _) = Treatments().MapBatchData(batch);
        var entries = Treatments().MapFoodsToConnectorEntries(batch);

        entries.Select(e => e.Entry.MealName).Should().Equal("B", "D");
        entries.Select(e => e.Entry.ExternalEntryId).Should().Equal(
            "glooko_food_639129672000000000_20", "glooko_food_639129672000000000_20_occurrence:1");
        carbs.Select(c => c.LegacyId).Should().OnlyHaveUniqueItems().And.HaveCount(2);
        entries.Select(e => e.CarbIntakeLegacyId).Should().Equal(carbs.Select(c => c.LegacyId));
    }

    [Fact]
    public void V2TwinsKeepTheirLabelsWhateverOrderGlookoSendsThem()
    {
        GlookoFood Twin(string name, double protein) =>
            new() { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 20, Description = name, Protein = protein };

        GlookoBatchData Batch(params GlookoFood[] foods) => new() { Foods = foods };

        var apple = Twin("Apple", 1);
        var bread = Twin("Bread", 4);
        var sameLabelLowProtein = Twin("Bread", 2);

        string[] LabelsById(GlookoBatchData batch) => [.. Treatments().MapFoodsToConnectorEntries(batch)
            .OrderBy(e => e.Entry.ExternalEntryId, StringComparer.Ordinal)
            .Select(e => $"{e.Entry.ExternalEntryId}={e.Entry.MealName}:{e.Entry.Protein}")];

        LabelsById(Batch(apple, bread, sameLabelLowProtein)).Should().Equal(
            LabelsById(Batch(bread, sameLabelLowProtein, apple)))
            .And.Equal(LabelsById(Batch(sameLabelLowProtein, apple, bread)));
        LabelsById(Batch(bread, apple)).Should().Equal(
            "glooko_food_639129672000000000_20=Apple:1",
            "glooko_food_639129672000000000_20_occurrence:1=Bread:4");
    }

    [Fact]
    public void V2RepeatedGuidlessFoodsGetTheSameIdsOnEveryRun()
    {
        var batch = new GlookoBatchData
        {
            Foods = [.. Enumerable.Range(0, 3).Select(_ => new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 20 })],
        };

        Treatments().MapBatchData(batch).carbs.Select(c => c.LegacyId)
            .Should().Equal(Treatments().MapBatchData(batch).carbs.Select(c => c.LegacyId));
        Treatments().MapFoodsToConnectorEntries(batch).Select(e => e.Entry.ExternalEntryId)
            .Should().Equal(Treatments().MapFoodsToConnectorEntries(batch).Select(e => e.Entry.ExternalEntryId));
    }

    [Fact]
    public void V3FirstGuidlessFoodKeepsItsStoredIdAndTheNextOnesAreToldApart()
    {
        var histories = new GlookoV3HistoriesResponse
        {
            Histories =
            [
                new GlookoV3HistoryEntry
                {
                    Type = "meals",
                    Item = new GlookoV3HistoryMeal
                    {
                        Guid = "meal-1", Timestamp = V2Time, Carbs = 60,
                        Foods =
                        [
                            new GlookoV3HistoryFood { Name = "Toast", Carbs = 20 },
                            new GlookoV3HistoryFood { Name = "Toast", Carbs = 20 },
                            new GlookoV3HistoryFood { Name = "Toast", Carbs = 20 },
                            new GlookoV3HistoryFood { Name = "Jam", Carbs = 10 },
                            new GlookoV3HistoryFood { Guid = "f-1", Name = "Toast", Carbs = 20 },
                        ],
                    },
                },
            ],
        };

        var entries = Treatments().MapV3HistoryMealsToConnectorEntries(histories);

        entries.Select(e => e.Entry.ExternalEntryId).Should().Equal(
            "v3meal_meal-1_Toast", "v3meal_meal-1_Toast_occurrence:1", "v3meal_meal-1_Toast_occurrence:2",
            "v3meal_meal-1_Jam", "f-1");
        entries.Select(e => e.CarbIntakeLegacyId).Should().OnlyContain(id => id == "glooko_v3meal_meal-1");
        entries.Select(e => e.Entry.ExternalEntryId)
            .Should().Equal(Treatments().MapV3HistoryMealsToConnectorEntries(histories).Select(e => e.Entry.ExternalEntryId));
    }

    [Fact]
    public void V3FoodNamesAreComparedTrimmedBecauseTheRepositoryStoresTheIdTrimmed()
    {
        var histories = new GlookoV3HistoriesResponse
        {
            Histories =
            [
                new GlookoV3HistoryEntry
                {
                    Type = "meals",
                    Item = new GlookoV3HistoryMeal
                    {
                        Guid = "meal-1", Timestamp = V2Time, Carbs = 40,
                        Foods =
                        [
                            new GlookoV3HistoryFood { Name = "Toast", Carbs = 20 },
                            new GlookoV3HistoryFood { Name = "Toast ", Carbs = 20 },
                        ],
                    },
                },
            ],
        };

        var ids = Treatments().MapV3HistoryMealsToConnectorEntries(histories).Select(e => e.Entry.ExternalEntryId).ToList();

        ids.Should().Equal("v3meal_meal-1_Toast", "v3meal_meal-1_Toast_occurrence:1");
        ids.Select(id => id.Trim()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void V3AFoodWithAnEmptyGuidIsGuidlessAsInV2()
    {
        var histories = new GlookoV3HistoriesResponse
        {
            Histories =
            [
                new GlookoV3HistoryEntry
                {
                    Type = "meals",
                    Item = new GlookoV3HistoryMeal
                    {
                        Guid = "meal-1", Timestamp = V2Time, Carbs = 20,
                        Foods = [new GlookoV3HistoryFood { Guid = "", Name = "Toast", Carbs = 20 }],
                    },
                },
            ],
        };

        Treatments().MapV3HistoryMealsToConnectorEntries(histories).Single().Entry.ExternalEntryId
            .Should().Be("v3meal_meal-1_Toast");
    }

    [Fact]
    public void V3TwinsKeepTheirLabelsWhateverOrderGlookoSendsThem()
    {
        GlookoV3HistoriesResponse Meal(params GlookoV3HistoryFood[] foods) => new()
        {
            Histories =
            [
                new GlookoV3HistoryEntry
                {
                    Type = "meals",
                    Item = new GlookoV3HistoryMeal { Guid = "meal-1", Timestamp = V2Time, Carbs = 40, Foods = foods },
                },
            ],
        };

        var small = new GlookoV3HistoryFood { Name = "Toast", Carbs = 10 };
        var large = new GlookoV3HistoryFood { Name = "Toast", Carbs = 30 };

        string[] CarbsById(GlookoV3HistoriesResponse histories) => [.. Treatments().MapV3HistoryMealsToConnectorEntries(histories)
            .OrderBy(e => e.Entry.ExternalEntryId, StringComparer.Ordinal)
            .Select(e => $"{e.Entry.ExternalEntryId}={e.Entry.Carbs}")];

        CarbsById(Meal(small, large)).Should().Equal(CarbsById(Meal(large, small)))
            .And.Equal("v3meal_meal-1_Toast=10", "v3meal_meal-1_Toast_occurrence:1=30");
    }

    [Fact]
    public void V3SameNamedFoodsOfDifferentMealsAreCountedPerMeal()
    {
        GlookoV3HistoryEntry Meal(string guid) => new()
        {
            Type = "meals",
            Item = new GlookoV3HistoryMeal
            {
                Guid = guid, Timestamp = V2Time, Carbs = 20,
                Foods = [new GlookoV3HistoryFood { Name = "Toast", Carbs = 20 }],
            },
        };

        var entries = Treatments().MapV3HistoryMealsToConnectorEntries(
            new GlookoV3HistoriesResponse { Histories = [Meal("meal-1"), Meal("meal-2")] });

        entries.Select(e => e.Entry.ExternalEntryId).Should().Equal("v3meal_meal-1_Toast", "v3meal_meal-2_Toast");
    }

    [Fact]
    public void V3FoodEntriesNameTheLegacyIdTheirMealCarbIntakeIsStoredUnder_WhenAMealRepeatsAFood()
    {
        var histories = new GlookoV3HistoriesResponse
        {
            Histories =
            [
                new GlookoV3HistoryEntry
                {
                    Type = "meals",
                    Item = new GlookoV3HistoryMeal
                    {
                        Guid = "meal-1", Timestamp = V2Time, Carbs = 40,
                        Foods =
                        [
                            new GlookoV3HistoryFood { Name = "Toast", Carbs = 20 },
                            new GlookoV3HistoryFood { Name = "Toast", Carbs = 20 },
                        ],
                    },
                },
            ],
        };

        var entries = Treatments().MapV3HistoryMealsToConnectorEntries(histories);

        entries.Should().HaveCount(2);
        entries.Select(e => e.Entry.ExternalEntryId).Should().OnlyHaveUniqueItems();
        entries.Sum(e => e.Entry.Carbs).Should().Be(40m);
        Treatments().MapV3HistoryMealsToCarbIntakes(histories).Single().Carbs.Should().Be(40);
    }

    [Fact]
    public void V3HistoryFoodEntriesNameTheLegacyIdTheirMealCarbIntakeIsStoredUnder()
    {
        var histories = new GlookoV3HistoriesResponse
        {
            Histories =
            [
                new GlookoV3HistoryEntry
                {
                    Type = "meals",
                    Item = new GlookoV3HistoryMeal
                    {
                        Guid = "meal-1", Timestamp = V2Time, Carbs = 50,
                        Foods = [new GlookoV3HistoryFood { Guid = "f-1", Name = "Toast", Carbs = 50 }],
                    },
                },
                new GlookoV3HistoryEntry
                {
                    Type = "meals",
                    Item = new GlookoV3HistoryMeal
                    {
                        Timestamp = V2Time, Carbs = 35,
                        Foods = [new GlookoV3HistoryFood { Guid = "f-2", Name = "Rice", Carbs = 35 }],
                    },
                },
            ],
        };

        var entries = Treatments().MapV3HistoryMealsToConnectorEntries(histories);

        entries.Select(e => e.CarbIntakeLegacyId).Should().Equal(
            "glooko_v3meal_meal-1", "glooko_dda59db23bd51c84d4fc4a402324d79507f71dca");
        entries.Select(e => e.CarbIntakeLegacyId).Should().Equal(
            Treatments().MapV3HistoryMealsToCarbIntakes(histories).Select(c => c.LegacyId));
    }

    [Fact]
    public void V3Boluses()
    {
        var graph = Graph(new GlookoV3Series
        {
            DeliveredBolus = [new GlookoV3BolusDataPoint { X = V3X, Data = new GlookoV3BolusData { DeliveredUnits = 2.5, CarbsInput = 30 } }],
        });

        var (boluses, carbs, _) = Treatments().MapV3Boluses(graph);

        boluses.Single().LegacyId.Should().Be("glooko_262f5fdf3e9707419938112b85d8316df5802b38");
        carbs.Single().LegacyId.Should().Be("glooko_c37c8ac91def8511fdb149b21b4db429a35eb003");
    }

    [Fact]
    public void V3CarbsDeviceEventsAndManualInsulin()
    {
        var graph = Graph(new GlookoV3Series
        {
            CarbAll = [new GlookoV3CarbDataPoint { X = V3X, Carbs = 45 }],
            ReservoirChange = [new GlookoV3ConsumableDataPoint { X = V3X }],
            SetSiteChange = [new GlookoV3ConsumableDataPoint { X = V3X }],
            CgmSensorChange = [new GlookoV3ConsumableDataPoint { X = V3X }],
            GkInsulinBasal = [new GlookoV3InsulinDataPoint { X = V3X, Value = 12, Name = "Lantus" }],
            GkInsulinBolus = [new GlookoV3InsulinDataPoint { X = V3X, Value = 4, Name = "Humalog" }],
        });

        Treatments().MapV3CarbAll(graph).Single().LegacyId.Should().Be("glooko_83132c1f5c2f2033358b4c538c9a3037a10768e2");
        Treatments().MapV3DeviceEvents(graph).Select(e => e.LegacyId).Should().Equal(
            "glooko_7ffc3777295a8eaf37b139d6bf6d768b1b8868c8",
            "glooko_c2c025014477855e8ebe679e7bbb34eb7f9425b9",
            "glooko_b7cb8f2d8f9c2d063b8795ccfe4ed8ebbf4bb641");
        var (basals, boluses) = Treatments().MapV3ManualInsulin(graph);
        basals.Single().LegacyId.Should().Be("glooko_e6b37a327dbc1ebac8d455a1eb090eae5c3aad74");
        boluses.Single().LegacyId.Should().Be("glooko_f38fe74e75a30924bbfeae51fc194a36c5fa07f3");
    }

    [Fact]
    public void V3HistoryMeals()
    {
        var histories = new GlookoV3HistoriesResponse
        {
            Histories =
            [
                new GlookoV3HistoryEntry { Type = "meals", Item = new GlookoV3HistoryMeal { Guid = "meal-1", Timestamp = V2Time, Carbs = 50 } },
                new GlookoV3HistoryEntry { Type = "meals", Item = new GlookoV3HistoryMeal { Timestamp = V2Time, Carbs = 35 } },
            ],
        };

        Treatments().MapV3HistoryMealsToCarbIntakes(histories).Select(c => c.LegacyId)
            .Should().Equal("glooko_v3meal_meal-1", "glooko_dda59db23bd51c84d4fc4a402324d79507f71dca");
    }

    [Fact]
    public void Ssv2Insulin()
    {
        GlookoInjectionInsulin[] basals =
        [
            new() { Timestamp = V2Time, PumpTimestamp = V2Time, InsulinDelivered = 12, Name = "Lantus", Guid = "ib-1" },
            new() { Timestamp = V2Time, PumpTimestamp = V2Time, InsulinDelivered = 10, Name = "Lantus" },
        ];
        GlookoInjectionInsulin[] boluses =
        [
            new() { Timestamp = V2Time, PumpTimestamp = V2Time, InsulinDelivered = 4, Name = "Humalog", Guid = "ibo-1" },
            new() { Timestamp = V2Time, PumpTimestamp = V2Time, InsulinDelivered = 3, Name = "Humalog" },
        ];
        GlookoSsv2InsulinEvent[] events =
        [
            new() { Insulin = 11, InsulinType = "long_acting", DisplayTime = V2Time, EventTime = V2Time, Guid = "ev-1" },
            new() { Insulin = 9, InsulinType = "long_acting", DisplayTime = V2Time, EventTime = V2Time },
            new() { Insulin = 4.5, InsulinType = "fast_acting", DisplayTime = V2Time, EventTime = V2Time, Guid = "ev-2" },
            new() { Insulin = 2.5, InsulinType = "fast_acting", DisplayTime = V2Time, EventTime = V2Time },
        ];

        var (injectedBasals, injectedBoluses) = Treatments().MapSsv2InjectionInsulin(basals, boluses);
        injectedBasals.Select(b => b.LegacyId)
            .Should().Equal("glooko_injection_basal_ib-1", "glooko_afe1fc4150a3a2164352cae73f09c14773843423");
        injectedBoluses.Select(b => b.LegacyId)
            .Should().Equal("glooko_injection_bolus_ibo-1", "glooko_c1866c1d9aa0566f8e7d8002bce7f3869555ca3a");

        var (eventBasals, eventBoluses) = Treatments().MapSsv2InsulinEvents(events);
        eventBasals.Select(b => b.LegacyId)
            .Should().Equal("glooko_insulin_event_basal_ev-1", "glooko_6ea6f9919e2626015c5f69173e91b88765066f00");
        eventBoluses.Select(b => b.LegacyId)
            .Should().Equal("glooko_insulin_event_bolus_ev-2", "glooko_5bb8c82a4976e8d565742c981301ab31e3a2364e");
    }

    [Fact]
    public void Ssv2CarbsAndExtendedBoluses()
    {
        GlookoSsv2CarbsEvent[] carbs =
        [
            new() { DisplayTime = V2Time, EventTime = V2Time, Timestamp = V2Time, CgmCarbs = 30, Guid = "c-1" },
            new() { DisplayTime = V2Time, EventTime = V2Time, Timestamp = V2Time, CgmCarbs = 15 },
        ];
        GlookoExtendedBolus[] extended =
        [
            new() { Timestamp = V2Time, PumpTimestamp = V2Time, InsulinDelivered = 3, Guid = "e-1" },
            new() { Timestamp = V2Time, PumpTimestamp = V2Time, InsulinDelivered = 2.25 },
        ];

        Treatments().MapSsv2CarbsEvents(carbs).Select(c => c.LegacyId)
            .Should().Equal("glooko_carbs_event_c-1", "glooko_c68f2dd9069e53cde078b47d0aaf1404da6965d3");
        Treatments().MapSsv2ExtendedBoluses(extended).Select(b => b.LegacyId)
            .Should().Equal("glooko_extended_bolus_e-1", "glooko_4f4b82c551a0cd3ffd6ae4d5c282ecd723612e54");
    }

    [Fact]
    public void TempBasalsKeys()
    {
        var graph = Graph(new GlookoV3Series
        {
            SuspendBasal = [new GlookoV3BasalDataPoint { X = V3X, Duration = 600 }],
            LgsPlgs = [new GlookoV3LgsPlgsDataPoint { X = V3X + 1, Duration = 600 }],
            TemporaryBasal = [new GlookoV3BasalDataPoint { X = V3X + 2, Y = 0.5, Duration = 600 }],
            ScheduledBasal = [new GlookoV3BasalDataPoint { X = V3X + 3, Y = 0.8, Duration = 600 }],
        });
        var batch = new GlookoBatchData
        {
            TempBasals = [new GlookoTempBasal { Timestamp = V2Time, Rate = 0.5, Duration = 600 }],
            ScheduledBasals = [new GlookoBasal { Timestamp = "2026-04-28T11:00:00Z", Rate = 0.8, Duration = 600 }],
            SuspendBasals = [new GlookoSuspendBasal { Timestamp = "2026-04-28T12:00:00Z", Duration = 600 }],
        };

        TempBasals().TransformV3ToTempBasals(graph).Select(t => t.LegacyId).Should().Equal(
            "glooko_suspend_basal_1779230416", "glooko_lgsplgs_basal_1779230417",
            "glooko_tempbasal_1779230418", "glooko_scheduledbasal_1779230419");
        TempBasals().TransformV2ToTempBasals(batch).Select(t => t.LegacyId).Should().Equal(
            "glooko_v2_tempbasal_639129672000000000", "glooko_v2_scheduledbasal_639129708000000000",
            "glooko_v2_suspend_basal_639129744000000000");
    }

    [Fact]
    public void StateSpansKeys()
    {
        var graph = Graph(new GlookoV3Series
        {
            SuspendBasal = [new GlookoV3BasalDataPoint { X = V3X, Duration = 600 }],
            LgsPlgs = [new GlookoV3LgsPlgsDataPoint { X = V3X + 1, Duration = 600 }],
            PumpCamapsEaseOffMode = [new GlookoV3PumpModeDataPoint { X = V3X, Type = "ease_off", Duration = 3600 }],
            PumpCamapsNoPumpConnectivityMode = [new GlookoV3PumpModeDataPoint { X = V3X, Type = "no_pump_connectivity", Duration = 3600 }],
        });
        var batch = new GlookoBatchData
        {
            SuspendBasals = [new GlookoSuspendBasal { Timestamp = "2026-04-28T12:00:00Z", Duration = 600 }],
        };

        StateSpans().TransformV3ToStateSpans(graph).Select(s => s.OriginalId)
            .Should().Equal("glooko_suspend_1779230416", "glooko_lgsplgs_1779230417");
        StateSpans().TransformV3PumpModeToStateSpans(graph).Select(s => s.OriginalId)
            .Should().Equal("glooko_pumpmode_camaps_easeoff_1779230416", "glooko_pumpconn_camaps_no_pump_1779230416");
        StateSpans().TransformV2ToStateSpans(batch).Select(s => s.OriginalId).Should().Equal("glooko_v2_suspend_639129744000000000");
    }

    [Fact]
    public void SystemEventsKeys()
    {
        var graph = Graph(new GlookoV3Series
        {
            PumpAlarm = [new GlookoV3AlarmDataPoint { X = V3X, Name = "Occlusion", AlarmSeverity = "hazard" }],
        });
        GlookoSsv2Alarm[] alarms =
        [
            new() { PumpTimestamp = V2Time, Value = "occlusion", AlarmType = "occlusion", Guid = "a-1" },
            new() { PumpTimestamp = V2Time, Value = "occlusion", AlarmType = "occlusion" },
        ];

        SystemEvents().TransformV3ToSystemEvents(graph).Single().OriginalId.Should().Be("glooko_alarm_1779230416");
        SystemEvents().TransformSsv2AlarmsToSystemEvents(alarms).Select(e => e.OriginalId)
            .Should().Equal("glooko_ssv2_alarm_a-1", "glooko_ssv2_alarm_raw_2026-04-28T10:00:00Z_occlusion");
    }

    [Fact]
    public void PumpEventsKeys()
    {
        GlookoPumpEvent[] events =
        [
            new() { Type = "reservoir_change", PumpTimestamp = V2Time, Guid = "p-1" },
            new() { Type = "reservoir_change", PumpTimestamp = V2Time },
        ];

        PumpEvents().TransformPumpEventsToDeviceEvents(events).Select(e => e.LegacyId)
            .Should().Equal("glooko_event_p-1", "glooko_event_raw_reservoir_change_2026-04-28T10:00:00Z");
    }

    [Fact]
    public void NotesAndActivities()
    {
        GlookoSsv2Note[] notes =
        [
            new() { Timestamp = V2Time, Value = "felt low", Guid = "n-1" },
            new() { Timestamp = V2Time, Value = "felt high" },
        ];
        GlookoSsv2Exercise[] exercises =
        [
            new() { Timestamp = V2Time, Name = "Run", Duration = 1800, Guid = "x-1" },
            new() { Timestamp = V2Time, Name = "Walk", Duration = 900 },
        ];
        GlookoSsv2ExerciseEvent[] exerciseEvents =
        [
            new() { DisplayTime = V2Time, EventTime = V2Time, Duration = 1800, Intensity = "high", Guid = "xe-1" },
            new() { DisplayTime = V2Time, EventTime = V2Time, Duration = 900, Intensity = "low" },
        ];

        Notes().MapSsv2Notes(notes).Select(n => n.LegacyId)
            .Should().Equal("glooko_note_n-1", "glooko_391be7b111204984cc133f7a2f8a29eed847c2c1");
        Activities().MapSsv2Exercises(exercises).Select(a => a.Id)
            .Should().Equal("glooko_exercise_x-1", "glooko_b401253e5605c1f5e5d3a57a408d76b6e5ba385c");
        Activities().MapSsv2ExerciseEvents(exerciseEvents).Select(a => a.Id)
            .Should().Equal("glooko_exercise_event_xe-1", "glooko_c301780ec11b45e61ca4173b2c0ecb41dacb7099");
    }

    [Fact]
    public void SyncIdentifiers()
    {
        var batch = new GlookoBatchData
        {
            Readings = [new GlookoCgmReading { Timestamp = "2026-04-28T10:05:00Z", Value = 12500 }],
            Foods = [new GlookoFood { Timestamp = V2Time, PumpTimestamp = V2Time, Carbs = 25.5 }],
        };
        var graph = Graph(new GlookoV3Series
        {
            ProfileChange = [new GlookoV3ProfileChangeDataPoint { X = V3X }],
        });
        var settings = GlookoProfileMapperActivationTests.Build(("device-1", [("2026-04-01T10:00:00Z", "A")]));

        SensorGlucose().TransformBatchDataToSensorGlucose(batch).Single().SyncIdentifier.Should().Be("glooko_cgm_raw_2026-04-28T10:05:00Z");
        Treatments().MapFoodsToConnectorEntries(batch).Single().Entry.ExternalEntryId.Should().Be("glooko_food_639129672000000000_25.5");
        StateSpans().TransformV3ToProfileActivations(graph).Single().SyncIdentifier.Should().Be("glooko_profile_1779230416");
        new GlookoProfileMapper(Source, NullLogger.Instance).TransformDeviceSettingsToProfileActivations(settings)
            .Single().SyncIdentifier.Should().Be("glooko_active_profile_device-1_1775037600000");
    }

    [Fact]
    public void V3DeviceSettingsProfile()
    {
        var settings = JsonSerializer.Deserialize<GlookoV3DeviceSettingsResponse>(
            """
            {"deviceSettings":{"pumps":{"pump-1":{"2026-01-10T00:00:00Z":{
              "basalSettings":{"activeBasalProgram":"Default"},
              "pumpProfilesBasal":[{"segments":{"profileName":"Default","current":true,
                "data":[{"segmentStart":0.0,"duration":24.0,"value":0.8}]}}]}}}}}
            """)!;

        new GlookoProfileMapper(Source, NullLogger.Instance).TransformDeviceSettingsToProfiles(settings)
            .Single().Id.Should().Be("glooko_1768003200000");
    }

    [Fact]
    public void Profiles()
    {
        var settings = GlookoSettingsProfileMapperTests.Sample();

        new GlookoSettingsProfileMapper(Source, NullLogger.Instance, TimeProvider.System)
            .TransformSettingsToProfile([settings])!.Id.Should().Be("glooko_1747019989859");
    }
}
