using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.API.Services.Treatments;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// Tests for the primary-record selection logic inside
/// <see cref="V4ToLegacyProjectionService.GetProjectedTreatmentsAsync"/> when a
/// single CorrelationId groups multiple boluses/carbs (N:M projection).
///
/// The service must pick the dominant-dose record as the primary "Meal Bolus",
/// with a deterministic Id tiebreaker so the output is stable across identical
/// requests regardless of the underlying storage-layer sort order.
/// </summary>
public class V4ToLegacyProjectionServiceTests
{
    private readonly Mock<ISensorGlucoseRepository> _sensorGlucoseRepo = new();
    private readonly Mock<IBolusRepository> _bolusRepo = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepo = new();
    private readonly Mock<IBGCheckRepository> _bgCheckRepo = new();
    private readonly Mock<INoteRepository> _noteRepo = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepo = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepo = new();
    private readonly Mock<IBolusCalculationRepository> _bolusCalcRepo = new();
    private readonly Mock<ITreatmentFoodService> _treatmentFoodService = new();
    private readonly NocturneDbContext _dbContext;
    private readonly V4ToLegacyProjectionService _service;

    public V4ToLegacyProjectionServiceTests()
    {
        // Empty defaults for the record types we don't exercise in these tests.
        _bgCheckRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<BGCheck>());

        _noteRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<Note>());

        _deviceEventRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<DeviceEvent>());

        _treatmentFoodService
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<TreatmentFood>());

        _tempBasalRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<TempBasal>());

        _bolusCalcRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<BolusCalculation>());

        _dbContext = TestDbContextFactory.CreateInMemoryContext();
        _dbContext.TenantId = TenantId; // satisfy the tenant query filter

        _service = CreateService(_dbContext);
    }

    private void SetupBoluses(IEnumerable<Bolus> boluses) =>
        _bolusRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<BolusKind?>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(boluses);

    private void SetupCarbs(IEnumerable<CarbIntake> carbs) =>
        _carbIntakeRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(carbs);

    [Fact]
    public async Task GetProjectedTreatments_MultipleBolusesInCorrelation_SelectsLargestAsMealBolus()
    {
        var correlationId = Guid.CreateVersion7();
        var timestamp = new DateTime(2025, 01, 01, 12, 0, 0, DateTimeKind.Utc);

        var smallBolus = new Bolus
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Insulin = 2.0,
        };
        var largeBolus = new Bolus
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Insulin = 5.0,
        };
        var carb = new CarbIntake
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Carbs = 45.0,
        };

        // Intentionally present boluses in ascending-insulin order so a naive
        // "take the first" selector would pick the 2u bolus.
        SetupBoluses(new[] { smallBolus, largeBolus });
        SetupCarbs(new[] { carb });

        var result = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();

        var mealBolus = result.Single(t => t.EventType == TreatmentTypes.MealBolus);
        mealBolus.Insulin.Should().Be(5.0);
        mealBolus.Id.Should().Be(largeBolus.Id.ToString());

        var correction = result.Single(t => t.EventType == TreatmentTypes.CorrectionBolus);
        correction.Insulin.Should().Be(2.0);
        correction.Id.Should().Be(smallBolus.Id.ToString());
    }

    [Fact]
    public async Task GetProjectedTreatments_AlgorithmBolus_ProjectsAutomaticFlag()
    {
        // An SMB / auto-bolus is stored with Kind=Algorithm + Automatic=true; a user-initiated
        // bolus with Automatic=false. Both must surface on the legacy `automatic` field so v1/v3
        // clients (LoopFollow, Trio import) can distinguish them. Each bolus is standalone
        // (distinct CorrelationId, no carbs) so both project as Correction Bolus.
        var timestamp = new DateTime(2025, 01, 01, 12, 0, 0, DateTimeKind.Utc);

        var smb = new Bolus
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            Timestamp = timestamp,
            Insulin = 0.3,
            Kind = BolusKind.Algorithm,
            Automatic = true,
        };
        var manual = new Bolus
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            Timestamp = timestamp.AddMinutes(5),
            Insulin = 4.0,
            Kind = BolusKind.Manual,
            Automatic = false,
        };

        SetupBoluses(new[] { smb, manual });
        SetupCarbs(Enumerable.Empty<CarbIntake>());

        var result = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();

        var smbTreatment = result.Single(t => t.Id == smb.Id.ToString());
        smbTreatment.EventType.Should().Be(TreatmentTypes.CorrectionBolus);
        smbTreatment.Automatic.Should().Be(true);

        var manualTreatment = result.Single(t => t.Id == manual.Id.ToString());
        manualTreatment.Automatic.Should().Be(false);
    }

    [Fact]
    public async Task GetProjectedTreatments_MultipleCarbsInCorrelation_SelectsLargestAsMealBolus()
    {
        var correlationId = Guid.CreateVersion7();
        var timestamp = new DateTime(2025, 01, 01, 12, 0, 0, DateTimeKind.Utc);

        var bolus = new Bolus
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Insulin = 4.0,
        };
        var smallCarb = new CarbIntake
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Carbs = 15.0,
        };
        var largeCarb = new CarbIntake
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Carbs = 60.0,
        };

        SetupBoluses(new[] { bolus });
        SetupCarbs(new[] { smallCarb, largeCarb });

        var result = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();

        var mealBolus = result.Single(t => t.EventType == TreatmentTypes.MealBolus);
        mealBolus.Carbs.Should().Be(60.0);

        var carbCorrection = result.Single(t => t.EventType == TreatmentTypes.CarbCorrection);
        carbCorrection.Carbs.Should().Be(15.0);
        carbCorrection.Id.Should().Be(smallCarb.Id.ToString());
    }

    [Fact]
    public async Task GetProjectedTreatments_EqualInsulinInCorrelation_TiebreaksByIdAscending()
    {
        // Two boluses with equal Insulin, equal Timestamp, equal CorrelationId.
        // The tiebreaker must be Id ascending — stable across request order.
        var correlationId = Guid.CreateVersion7();
        var timestamp = new DateTime(2025, 01, 01, 12, 0, 0, DateTimeKind.Utc);

        // Deterministic, easily-ordered Ids.
        var lowId = new Guid("00000000-0000-0000-0000-000000000001");
        var highId = new Guid("00000000-0000-0000-0000-000000000002");

        var b1 = new Bolus
        {
            Id = lowId,
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Insulin = 3.0,
        };
        var b2 = new Bolus
        {
            Id = highId,
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Insulin = 3.0,
        };
        var carb = new CarbIntake
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Carbs = 45.0,
        };

        // First invocation: b1 before b2.
        SetupBoluses(new[] { b1, b2 });
        SetupCarbs(new[] { carb });
        var result1 = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();
        var mealBolus1 = result1.Single(t => t.EventType == TreatmentTypes.MealBolus);

        // Second invocation: input order reversed. Output must be identical.
        SetupBoluses(new[] { b2, b1 });
        var result2 = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();
        var mealBolus2 = result2.Single(t => t.EventType == TreatmentTypes.MealBolus);

        mealBolus1.Id.Should().Be(mealBolus2.Id);
        // And specifically, the lower-Id record wins.
        mealBolus1.Id.Should().Be(lowId.ToString());
    }

    [Fact]
    public async Task GetProjectedTreatments_EqualCarbsInCorrelation_TiebreaksByIdAscending()
    {
        var correlationId = Guid.CreateVersion7();
        var timestamp = new DateTime(2025, 01, 01, 12, 0, 0, DateTimeKind.Utc);

        var lowId = new Guid("00000000-0000-0000-0000-000000000001");
        var highId = new Guid("00000000-0000-0000-0000-000000000002");

        var bolus = new Bolus
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Insulin = 4.0,
        };
        var c1 = new CarbIntake
        {
            Id = lowId,
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Carbs = 30.0,
        };
        var c2 = new CarbIntake
        {
            Id = highId,
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Carbs = 30.0,
        };

        SetupBoluses(new[] { bolus });
        SetupCarbs(new[] { c1, c2 });
        var result1 = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();

        SetupCarbs(new[] { c2, c1 });
        var result2 = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();

        // The Meal Bolus projection carries the primary bolus Id, not the carb
        // Id, so the pairing's stability is verified through the leftover
        // CarbCorrection: the higher-Id carb must always be the leftover.
        var leftover1 = result1.Single(t => t.EventType == TreatmentTypes.CarbCorrection);
        var leftover2 = result2.Single(t => t.EventType == TreatmentTypes.CarbCorrection);
        leftover1.Id.Should().Be(leftover2.Id).And.Be(highId.ToString());
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_ExcludesRecordAtCursor()
    {
        // AAPS passes the timestamp of the newest record it already holds as the cursor.
        // The history query must be strictly-greater (>), so the record AT the cursor is
        // not re-returned; an inclusive bound loops AAPS re-requesting the same page.
        var atCursor = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Timestamp = Cursor,
            Insulin = 1.0,
        };
        var newer = Guid.CreateVersion7();
        var afterCursor = new BolusEntity
        {
            Id = newer,
            TenantId = TenantId,
            Timestamp = Cursor.AddMinutes(1),
            Insulin = 2.0,
        };

        await AddModifiedAsync(
            (atCursor, Cursor), // exactly at the cursor -> must be excluded
            (afterCursor, Cursor.AddMilliseconds(1))); // strictly newer -> must be returned

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100)).ToList();

        result.Should().ContainSingle();
        result[0].Id.Should().Be(newer.ToString());
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_CorrelatedBolusAndCarb_ProjectOneMealBolusWithFoods()
    {
        // The range path pairs a correlated bolus + carb intake into a single Meal Bolus carrying
        // the food breakdown. The modified-since surface must agree, or a v3 sync client sees two
        // treatments (and no foods) for a meal the range read shows as one.
        var correlationId = Guid.CreateVersion7();
        var bolusId = Guid.CreateVersion7();
        var carbId = Guid.CreateVersion7();

        var bolus = new BolusEntity
        {
            Id = bolusId,
            TenantId = TenantId,
            Timestamp = Cursor,
            Insulin = 3.5,
            CorrelationId = correlationId,
        };
        var carb = new CarbIntakeEntity
        {
            Id = carbId,
            TenantId = TenantId,
            Timestamp = Cursor,
            Carbs = 42.0,
            CorrelationId = correlationId,
        };
        await AddModifiedAsync((bolus, Cursor.AddMinutes(1)), (carb, Cursor.AddMinutes(1)));

        _treatmentFoodService
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TreatmentFood
                {
                    CarbIntakeId = carbId,
                    FoodName = "Pizza",
                    Portions = 2m,
                    FatPerPortion = 6m,
                    ProteinPerPortion = 4m,
                },
            });

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100)).ToList();

        result.Should().ContainSingle();
        result[0].EventType.Should().Be(TreatmentTypes.MealBolus);
        result[0].Id.Should().Be(bolusId.ToString());
        result[0].Insulin.Should().Be(3.5);
        result[0].Carbs.Should().Be(42.0);
        result[0].FoodType.Should().Be("Pizza");
        result[0].Fat.Should().Be(12.0);
        result[0].Protein.Should().Be(8.0);
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_MealPair_StampsTheNewerOfTheTwoModifications()
    {
        // The consumer advances its cursor to max(srvModified) over the page. Stamping the meal with
        // the older of the pair leaves the other record above that cursor, so the next request
        // re-serves the same meal with the same stamp and the cursor never moves.
        var correlationId = Guid.CreateVersion7();
        var bolus = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Timestamp = Cursor,
            Insulin = 1.0,
            CorrelationId = correlationId,
        };
        var carb = new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Timestamp = Cursor,
            Carbs = 10.0,
            CorrelationId = correlationId,
        };
        var newest = Cursor.AddMinutes(5);
        await AddModifiedAsync((bolus, Cursor.AddMinutes(1)), (carb, newest));

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100)).ToList();

        result.Should().ContainSingle();
        result[0].SrvModified.Should().Be(new DateTimeOffset(newest, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_LegacyOriginatedRecord_IsProjected()
    {
        // A treatment uploaded through v1/v2/v3 is stored as a V4 record with LegacyId set, and
        // there is no legacy treatments table for it to duplicate. Filtering those out would hide
        // every client-uploaded treatment from the v3 history sync.
        var legacyOriginated = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Timestamp = Cursor,
            Insulin = 2.0,
            LegacyId = "65f0c0ffee0000000000cafe",
        };
        await AddModifiedAsync((legacyOriginated, Cursor.AddMinutes(1)));

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100)).ToList();

        result.Should().ContainSingle();
        result[0].Id.Should().Be(legacyOriginated.Id.ToString());
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_FailingType_StillProjectsTheOthers()
    {
        // One type's read blowing up must degrade that type only, exactly as the range path does.
        var bolus = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Timestamp = Cursor,
            Insulin = 1.0,
        };
        var note = new NoteEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Timestamp = Cursor,
            Text = "survivor",
        };
        await AddModifiedAsync((bolus, Cursor.AddMinutes(1)), (note, Cursor.AddMinutes(1)));

        // A set whose own context is gone throws when read, leaving the other six types intact.
        using var abandoned = TestDbContextFactory.CreateInMemoryContext();
        var abandonedSet = abandoned.Set<BolusEntity>();
        await abandoned.DisposeAsync();
        _dbContext.Boluses = abandonedSet;

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100)).ToList();

        result.Should().ContainSingle();
        result[0].Id.Should().Be(note.Id.ToString());
    }

    [Fact]
    public async Task GetProjectedTreatments_FailingType_StillProjectsTheOthers()
    {
        SetupBoluses(Enumerable.Empty<Bolus>());
        _bolusRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<BolusKind?>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bolus read failed"));

        var note = new Note { Id = Guid.CreateVersion7(), Timestamp = Cursor, Text = "survivor" };
        _noteRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { note });

        var result = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();

        result.Should().ContainSingle();
        result[0].Id.Should().Be(note.Id.ToString());
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_OverLimit_ReturnsTheGloballyOldestPage()
    {
        // Each type contributes its own oldest `limit` rows and the merge is then cut to `limit`, so
        // the page is the globally-oldest window and never a multiple of the requested limit.
        var notes = Enumerable.Range(1, 3)
            .Select(i => (Entity: (object)new NoteEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                Timestamp = Cursor,
                Text = $"note-{i}",
            }, Modified: Cursor.AddMinutes(i * 2)))
            .ToList();
        var bgChecks = Enumerable.Range(1, 3)
            .Select(i => (Entity: (object)new BGCheckEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                Timestamp = Cursor,
                Glucose = 100 + i,
            }, Modified: Cursor.AddMinutes(i * 2 - 1)))
            .ToList();

        await AddModifiedAsync([.. notes, .. bgChecks]);

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 2)).ToList();

        result.Should().HaveCount(2);
        result.Select(t => t.Id).Should().Equal(
            ((BGCheckEntity)bgChecks[0].Entity).Id.ToString(),
            ((NoteEntity)notes[0].Entity).Id.ToString());
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_PairStraddlingThePageCut_LosesNeitherConstituent()
    {
        // The page is cut on raw row stamps before pairing. Cutting after pairing let the meal carry
        // the carb's far-newer stamp past the cut while the cursor still advanced past the bolus's
        // own row, so the insulin record was never fetched again.
        var correlationId = Guid.CreateVersion7();
        var bolus = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Timestamp = Cursor,
            Insulin = 4.0,
            CorrelationId = correlationId,
        };
        var carb = new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Timestamp = Cursor,
            Carbs = 30.0,
            CorrelationId = correlationId,
        };
        var notes = SeedNotes(3);

        await AddModifiedAsync(
            (bolus, Cursor.AddMinutes(1)),
            (carb, Cursor.AddMinutes(100)),
            (notes[0], Cursor.AddMinutes(2)),
            (notes[1], Cursor.AddMinutes(3)),
            (notes[2], Cursor.AddMinutes(4)));

        var delivered = await WalkHistoryAsync(limit: 3, mealCarbIntakeId: carb.Id);

        delivered.Should().BeEquivalentTo(new[]
        {
            bolus.Id, carb.Id, notes[0].Id, notes[1].Id, notes[2].Id,
        });
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_PairInsideThePage_MergesAndAdvancesPastBoth()
    {
        var correlationId = Guid.CreateVersion7();
        var bolus = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Timestamp = Cursor,
            Insulin = 4.0,
            CorrelationId = correlationId,
        };
        var carb = new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Timestamp = Cursor,
            Carbs = 30.0,
            CorrelationId = correlationId,
        };
        var notes = SeedNotes(1);

        await AddModifiedAsync(
            (bolus, Cursor.AddMinutes(1)),
            (carb, Cursor.AddMinutes(2)),
            (notes[0], Cursor.AddMinutes(3)));

        var firstPage = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 3)).ToList();

        firstPage.Should().HaveCount(2);
        firstPage.Should().ContainSingle(t => t.EventType == TreatmentTypes.MealBolus)
            .Which.Id.Should().Be(bolus.Id.ToString());

        var delivered = await WalkHistoryAsync(limit: 3, mealCarbIntakeId: carb.Id);

        delivered.Should().BeEquivalentTo(new[] { bolus.Id, carb.Id, notes[0].Id });
    }

    /// <summary>
    /// Walks the history surface the way a v3 sync client does — advancing the cursor to
    /// <c>max(srvModified)</c> over each page — and returns every record id it was handed, counting
    /// a Meal Bolus as delivering both of its constituents. Duplicates and omissions both survive
    /// into the result, so one equivalence assertion covers re-sends, loops and skipped rows.
    /// </summary>
    private async Task<List<Guid>> WalkHistoryAsync(int limit, Guid? mealCarbIntakeId = null)
    {
        var delivered = new List<Guid>();
        var cursor = CursorMills;

        for (var page = 0; page < 10; page++)
        {
            var treatments = (await _service.GetProjectedTreatmentsModifiedSinceAsync(cursor, limit)).ToList();
            if (treatments.Count == 0)
                break;

            foreach (var treatment in treatments)
            {
                delivered.Add(Guid.Parse(treatment.Id!));
                if (treatment.EventType == TreatmentTypes.MealBolus && mealCarbIntakeId.HasValue)
                    delivered.Add(mealCarbIntakeId.Value);
            }

            cursor = treatments.Max(t => t.SrvModified ?? t.Mills);
        }

        return delivered;
    }

    private static List<NoteEntity> SeedNotes(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new NoteEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                Timestamp = Cursor,
                Text = $"note-{i}",
            })
            .ToList();

    private static readonly DateTime Cursor = new(2025, 01, 01, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static long CursorMills => new DateTimeOffset(Cursor, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>
    /// Persists entities and then pins their <c>SysUpdatedAt</c>: <c>SaveChanges</c> stamps
    /// <c>UtcNow</c> on insert, and a second save that touches only that column keeps the assigned
    /// value, letting a test place rows either side of the cursor.
    /// </summary>
    private async Task AddModifiedAsync(params (object Entity, DateTime Modified)[] rows)
    {
        foreach (var (entity, _) in rows)
            _dbContext.Add(entity);
        await _dbContext.SaveChangesAsync();

        foreach (var (entity, modified) in rows)
            ((ISystemTimestamped)entity).SysUpdatedAt = modified;
        await _dbContext.SaveChangesAsync();
    }

    private V4ToLegacyProjectionService CreateService(NocturneDbContext dbContext) =>
        new(
            _sensorGlucoseRepo.Object,
            _bolusRepo.Object,
            _carbIntakeRepo.Object,
            _bgCheckRepo.Object,
            _noteRepo.Object,
            _deviceEventRepo.Object,
            _tempBasalRepo.Object,
            _bolusCalcRepo.Object,
            _treatmentFoodService.Object,
            dbContext,
            NullLogger<V4ToLegacyProjectionService>.Instance
        );
}
