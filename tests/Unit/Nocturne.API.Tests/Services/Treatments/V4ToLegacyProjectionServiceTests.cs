using Nocturne.Infrastructure.Data.Services;
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
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

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
        _bgCheckRepo.As<IV4Repository<BGCheck>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<BGCheck>());

        _noteRepo.As<IV4Repository<Note>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<Note>());

        _deviceEventRepo.As<IV4Repository<DeviceEvent>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<DeviceEvent>());

        _treatmentFoodService
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<TreatmentFood>());

        _tempBasalRepo.As<IV4Repository<TempBasal>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<TempBasal>());

        _bolusCalcRepo.As<IV4Repository<BolusCalculation>>()
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
        _bolusRepo.As<IV4Repository<Bolus>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(boluses);

    private void SetupCarbs(IEnumerable<CarbIntake> carbs) =>
        _carbIntakeRepo.As<IV4Repository<CarbIntake>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(carbs);

    [Fact]
    public async Task GetProjectedTreatments_Ascending_ReadsEachTableFromItsOldestEnd_AndReturnsOldestFirst()
    {
        var start = new DateTime(2025, 01, 01, 12, 0, 0, DateTimeKind.Utc);
        var boluses = Enumerable.Range(0, 3)
            .Select(i => new Bolus { Id = Guid.CreateVersion7(), Timestamp = start.AddMinutes(i), Insulin = 1.0 + i })
            .ToList();
        _bolusRepo.As<IV4Repository<Bolus>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? _, DateTime? _, string? _, string? _, int limit, int _, bool _,
                    CancellationToken _) =>
                boluses.OrderBy(b => b.Timestamp).Take(limit).ToList());

        var result = (await _service.GetProjectedTreatmentsAsync(null, null, 2, descending: false)).ToList();

        result.Select(t => t.Insulin).Should().Equal(1.0, 2.0);
        _bolusRepo.As<IV4Repository<Bolus>>().Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetProjectedTreatments_Ascending_MergesTablesAndKeepsTheOldestRowsOfEachBeforeTheLimit()
    {
        var start = new DateTime(2025, 01, 01, 12, 0, 0, DateTimeKind.Utc);
        var boluses = new[] { 0, 2, 4, 6 }
            .Select(i => new Bolus { Id = Guid.CreateVersion7(), Timestamp = start.AddMinutes(i), Insulin = 1.0 })
            .ToList();
        var notes = new[] { 1, 3, 5, 7 }
            .Select(i => new Note { Id = Guid.CreateVersion7(), Timestamp = start.AddMinutes(i), Text = $"n{i}" })
            .ToList();
        _bolusRepo.As<IV4Repository<Bolus>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? _, DateTime? _, string? _, string? _, int limit, int _, bool descending,
                    CancellationToken _) =>
                (descending ? boluses.OrderByDescending(b => b.Timestamp) : boluses.OrderBy(b => b.Timestamp)).Take(limit).ToList());
        _noteRepo.As<IV4Repository<Note>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? _, DateTime? _, string? _, string? _, int limit, int _, bool descending, CancellationToken _) =>
                (descending ? notes.OrderByDescending(n => n.Timestamp) : notes.OrderBy(n => n.Timestamp)).Take(limit).ToList());

        var result = (await _service.GetProjectedTreatmentsAsync(
            null, null, 5, descending: false)).ToList();

        result.Select(t => t.Mills).Should().Equal(
            Enumerable.Range(0, 5).Select(i => new DateTimeOffset(start.AddMinutes(i)).ToUnixTimeMilliseconds()));
    }

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
    public async Task GetProjectedTreatments_ReportsServerClockNotEventTime()
    {
        // A meal is created when its first constituent was and modified when its last one was, so
        // srvCreated never runs ahead of srvModified.
        var correlationId = Guid.CreateVersion7();
        var eventTime = new DateTime(2025, 01, 01, 12, 0, 0, DateTimeKind.Utc);
        var mealBolus = new Bolus
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = eventTime,
            Insulin = 4.0,
            CreatedAt = eventTime.AddDays(2),
            ModifiedAt = eventTime.AddDays(4),
        };
        var carb = new CarbIntake
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = eventTime,
            Carbs = 30.0,
            CreatedAt = eventTime.AddDays(1),
            ModifiedAt = eventTime.AddDays(3),
        };
        var correction = new Bolus
        {
            Id = Guid.CreateVersion7(),
            Timestamp = eventTime.AddHours(1),
            Insulin = 1.0,
            CreatedAt = eventTime.AddDays(5),
            ModifiedAt = eventTime.AddDays(6),
        };
        SetupBoluses(new[] { mealBolus, correction });
        SetupCarbs(new[] { carb });

        var result = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();

        var meal = result.Single(t => t.EventType == TreatmentTypes.MealBolus);
        meal.SrvCreated.Should().Be(carb.CreatedAt.ToMills());
        meal.SrvModified.Should().Be(mealBolus.ModifiedAt.ToMills());

        var single = result.Single(t => t.EventType == TreatmentTypes.CorrectionBolus);
        single.SrvCreated.Should().Be(correction.CreatedAt.ToMills());
        single.SrvModified.Should().Be(correction.ModifiedAt.ToMills());
        single.Mills.Should().Be(correction.Timestamp.ToMills());
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_ReportsSrvCreatedAsTheRowCreationTime()
    {
        var bolus = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Timestamp = Cursor.AddYears(-1),
            Insulin = 1.0,
        };
        await AddModifiedAsync((bolus, Cursor.AddMinutes(1)));

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100)).ToList();

        result.Should().ContainSingle();
        result[0].SrvCreated.Should().Be(bolus.SysCreatedAt.ToMills());
        result[0].SrvCreated.Should().NotBe(result[0].Mills);
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
    public async Task GetProjectedTreatmentsModifiedSince_DeletedRecords_AreProjectedWithIsValidFalse()
    {
        // Nightscout's v3 history returns a deleted document with isValid: false and its delete as
        // srvModified; without it a history-syncing client keeps what was deleted elsewhere.
        var deletedAt = Cursor.AddMinutes(2);
        var deletedNote = new NoteEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = Cursor, Text = "removed", DeletedAt = deletedAt,
        };
        var liveNote = new NoteEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = Cursor, Text = "kept",
        };
        await AddModifiedAsync((deletedNote, deletedAt), (liveNote, Cursor.AddMinutes(1)));

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100)).ToList();

        result.Select(t => (t.Id, t.IsValid, t.SrvModified)).Should().Equal(
            (liveNote.Id.ToString(), (bool?)null, new DateTimeOffset(Cursor.AddMinutes(1), TimeSpan.Zero).ToUnixTimeMilliseconds()),
            (deletedNote.Id.ToString(), (bool?)false, new DateTimeOffset(deletedAt, TimeSpan.Zero).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_DeletedMeal_IsOneDeletedMealBolus()
    {
        var correlationId = Guid.CreateVersion7();
        var deletedAt = Cursor.AddMinutes(3);
        var bolus = new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = Cursor, Insulin = 4.0,
            CorrelationId = correlationId, DeletedAt = deletedAt,
        };
        var carb = new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = Cursor, Carbs = 40.0,
            CorrelationId = correlationId, DeletedAt = deletedAt,
        };
        await AddModifiedAsync((bolus, deletedAt), (carb, deletedAt));

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100)).ToList();

        var meal = result.Should().ContainSingle().Subject;
        meal.EventType.Should().Be(TreatmentTypes.MealBolus);
        meal.Id.Should().Be(bolus.Id.ToString());
        meal.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_DeletedAndLiveConstituents_DoNotPair()
    {
        var correlationId = Guid.CreateVersion7();
        var bolus = new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = Cursor, Insulin = 4.0,
            CorrelationId = correlationId, DeletedAt = Cursor.AddMinutes(2),
        };
        var carb = new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = Cursor, Carbs = 40.0,
            CorrelationId = correlationId,
        };
        await AddModifiedAsync((bolus, Cursor.AddMinutes(2)), (carb, Cursor.AddMinutes(1)));

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100)).ToList();

        result.Select(t => (t.EventType, t.IsValid)).Should().Equal(
            (TreatmentTypes.CarbCorrection, (bool?)null),
            (TreatmentTypes.CorrectionBolus, (bool?)false));
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_MealBolusDeleted_ResendsTheSurvivingCarbsUnderTheirOwnId()
    {
        // The client knew the meal under the bolus id. The carbs now read as a standalone treatment
        // under their own id, and their row is older than the cursor.
        var deletedAt = Cursor.AddMinutes(2);
        var (bolus, carb) = await AddMealAsync(
            bolusDeletedAt: deletedAt, bolusModified: deletedAt,
            carbDeletedAt: null, carbModified: Cursor.AddMinutes(-5));

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100)).ToList();

        result.Select(t => (t.Id, t.EventType, t.IsValid, t.SrvModified)).Should().BeEquivalentTo(new[]
        {
            (bolus.Id.ToString(), TreatmentTypes.CorrectionBolus, (bool?)false, Mills(deletedAt)),
            (carb.Id.ToString(), TreatmentTypes.CarbCorrection, (bool?)null, Mills(deletedAt)),
        });
        result.Max(t => t.SrvModified).Should().Be(Mills(deletedAt));
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_MealCarbsDeleted_ResendsTheSurvivingBolusUnderTheMealId()
    {
        // The client knew the meal under the bolus id, carbs included. The bolus now reads without
        // them under that same id, and its row is older than the cursor.
        var deletedAt = Cursor.AddMinutes(2);
        var (bolus, carb) = await AddMealAsync(
            bolusDeletedAt: null, bolusModified: Cursor.AddMinutes(-5),
            carbDeletedAt: deletedAt, carbModified: deletedAt);

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100)).ToList();

        var survivor = result.Should().ContainSingle(t => t.Id == bolus.Id.ToString()).Subject;
        survivor.EventType.Should().Be(TreatmentTypes.CorrectionBolus);
        survivor.Carbs.Should().BeNull();
        survivor.IsValid.Should().BeNull();
        survivor.SrvModified.Should().Be(Mills(deletedAt));
        result.Should().ContainSingle(t => t.Id == carb.Id.ToString()).Which.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_BothMealHalvesDeletedApart_ReadTogether_TombstoneTheMeal()
    {
        var bolusDeletedAt = Cursor.AddMinutes(2);
        var carbDeletedAt = Cursor.AddMinutes(4);
        var (bolus, _) = await AddMealAsync(
            bolusDeletedAt: bolusDeletedAt, bolusModified: bolusDeletedAt,
            carbDeletedAt: carbDeletedAt, carbModified: carbDeletedAt);

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100)).ToList();

        var meal = result.Should().ContainSingle().Subject;
        (meal.Id, meal.EventType, meal.IsValid, meal.SrvModified)
            .Should().Be((bolus.Id.ToString(), TreatmentTypes.MealBolus, (bool?)false, Mills(carbDeletedAt)));
    }

    private async Task<(BolusEntity Bolus, CarbIntakeEntity Carb)> AddMealAsync(
        DateTime? bolusDeletedAt, DateTime bolusModified, DateTime? carbDeletedAt, DateTime carbModified)
    {
        var correlationId = Guid.CreateVersion7();
        var bolus = new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = Cursor.AddHours(-1), Insulin = 4.0,
            CorrelationId = correlationId, DeletedAt = bolusDeletedAt,
        };
        var carb = new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = Cursor.AddHours(-1), Carbs = 40.0,
            CorrelationId = correlationId, DeletedAt = carbDeletedAt,
        };
        await AddModifiedAsync((bolus, bolusModified), (carb, carbModified));
        return (bolus, carb);
    }

    private static long Mills(DateTime value) => new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_LegacyOriginatedRecord_IsProjected()
    {
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
    public async Task GetProjectedTreatmentsModifiedSince_FailingType_FailsTheRead()
    {
        // A database failure on one type fails the read: serving the page without that type would
        // advance a history client's cursor past records it never received.
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
        var service = CreateService(new TestNocturneDbContextFactory(_dbContext) { Prepare = c => c.Boluses = abandonedSet });

        var read = () => service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100);

        await read.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_UntranslatableType_StillProjectsTheOthers()
    {
        // The in-memory provider cannot translate the served-span JSON filter, so the three span
        // types are skipped and every record type still serves.
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

        var result = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 100)).ToList();

        result.Select(t => t.Id).Should().BeEquivalentTo(new[] { bolus.Id.ToString(), note.Id.ToString() });
    }

    [Fact]
    public async Task GetProjectedTreatments_UntranslatableType_StillProjectsTheOthers()
    {
        SetupBoluses(Enumerable.Empty<Bolus>());
        _bolusRepo.As<IV4Repository<Bolus>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bolus read failed"));

        var note = new Note { Id = Guid.CreateVersion7(), Timestamp = Cursor, Text = "survivor" };
        _noteRepo.As<IV4Repository<Note>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { note });

        var result = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();

        result.Should().ContainSingle();
        result[0].Id.Should().Be(note.Id.ToString());
    }

    public static TheoryData<Exception> UnguardedReadFailures => new()
    {
        new OperationCanceledException("request aborted"),
        new TimeoutException("database timed out"),
    };

    /// <summary>
    /// Only a read the provider cannot translate is skipped; a cancellation or a database failure
    /// fails the read instead of serving a page with that type silently missing.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnguardedReadFailures))]
    public async Task GetProjectedTreatments_ReadFailureOtherThanTranslation_Propagates(Exception failure)
    {
        _bolusRepo.As<IV4Repository<Bolus>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var read = () => _service.GetProjectedTreatmentsAsync(null, null, 100);

        (await read.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
    }

    /// <summary>
    /// Resolving a span by id is not guarded: a failed lookup must not read as "no such treatment",
    /// which would answer a GET, PUT or DELETE by that id with 404.
    /// </summary>
    [Fact]
    public async Task GetProjectedStateSpanTreatment_LookupFailure_Propagates()
    {
        using var abandoned = TestDbContextFactory.CreateInMemoryContext();
        var abandonedSet = abandoned.Set<StateSpanEntity>();
        await abandoned.DisposeAsync();
        var service = CreateService(new TestNocturneDbContextFactory(_dbContext) { Prepare = c => c.StateSpans = abandonedSet });

        var lookup = () => service.GetProjectedStateSpanTreatmentAsync(Guid.NewGuid().ToString());

        await lookup.Should().ThrowAsync<ObjectDisposedException>();
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

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_MillisecondWithMoreThanLimitRows_ComesBackInOnePage()
    {
        // A millisecond holding at least `limit` rows must not be re-served: the cursor is that
        // millisecond, so a page cut inside it never advances. The page is extended to the end of
        // the millisecond instead.
        var after = Cursor.AddMinutes(1);
        var notes = Enumerable.Range(1, 7)
            .Select(i => (Entity: (object)new NoteEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                Timestamp = after,
                Text = $"note-{i}",
            }, Modified: after.AddTicks(i)))
            .ToList();
        await AddModifiedAsync([.. notes]);

        var first = (await _service.GetProjectedTreatmentsModifiedSinceAsync(CursorMills, 2)).ToList();

        first.Should().HaveCount(7);
        first.Select(t => Guid.Parse(t.Id!)).Should().BeEquivalentTo(
            notes.Select(n => ((NoteEntity)n.Entity).Id));

        var cursor = first.Max(t => t.SrvModified ?? t.Mills);
        var second = (await _service.GetProjectedTreatmentsModifiedSinceAsync(cursor, 2)).ToList();

        second.Should().BeEmpty();
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_TieGroupSplitByLimit_DeliveredOnceAcrossPages()
    {
        // Three rows share a millisecond; limit 2 would cut the group and, on the old strictly-greater
        // query, re-serve its rows while the cursor stayed put.
        var after = Cursor.AddMinutes(1);
        var tieGroup = Enumerable.Range(1, 3)
            .Select(i => Guid.CreateVersion7())
            .ToList();
        var later = Guid.CreateVersion7();
        await AddModifiedAsync(
            (new NoteEntity { Id = tieGroup[0], TenantId = TenantId, Timestamp = after, Text = "a" }, after.AddTicks(10)),
            (new NoteEntity { Id = tieGroup[1], TenantId = TenantId, Timestamp = after, Text = "b" }, after.AddTicks(20)),
            (new NoteEntity { Id = tieGroup[2], TenantId = TenantId, Timestamp = after, Text = "c" }, after.AddTicks(30)),
            (new NoteEntity { Id = later, TenantId = TenantId, Timestamp = after, Text = "d" }, after.AddMinutes(1)));

        var delivered = await WalkHistoryAsync(limit: 2);

        delivered.Should().BeEquivalentTo(tieGroup.Append(later));
        delivered.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task GetProjectedTreatmentsModifiedSince_MergeDoesNotCutOneTablesTieGroupWhileAnotherFillsThePage()
    {
        // The merged page is cut to `limit`; BGChecks fill it, so the Note tie group in the same
        // millisecond is left behind the cut. The extension must still deliver it, or the next
        // request (cursor at that millisecond) skips it forever.
        var after = Cursor.AddMinutes(1);
        var notes = Enumerable.Range(1, 2)
            .Select(i => new NoteEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                Timestamp = after,
                Text = $"note-{i}",
            })
            .ToList();
        var bgChecks = Enumerable.Range(1, 3)
            .Select(i => new BGCheckEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                Timestamp = after,
                Glucose = 100 + i,
            })
            .ToList();
        await AddModifiedAsync(
            (notes[0], after.AddTicks(10)),
            (notes[1], after.AddTicks(20)),
            (bgChecks[0], after.AddTicks(1)),
            (bgChecks[1], after.AddTicks(2)),
            (bgChecks[2], after.AddTicks(3)));

        var delivered = await WalkHistoryAsync(limit: 2);

        delivered.Should().BeEquivalentTo(
            notes.Select(n => n.Id).Concat(bgChecks.Select(b => b.Id)));
        delivered.Should().OnlyHaveUniqueItems();
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

    /// <summary>
    /// Each table counts a distinct power of two, so a table left out, or a count that replaces the
    /// running total, gives a different sum. The state spans are pinned by
    /// <c>TreatmentCountIntegrationTests</c>: their filters do not run in memory.
    /// </summary>
    [Fact]
    public async Task CountProjectedTreatments_AddsEveryTableOverTheWindow()
    {
        var (from, to) = (new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        _bolusRepo.Setup(r => r.CountAsync(from, to, It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _bgCheckRepo.Setup(r => r.CountAsync(from, to, It.IsAny<CancellationToken>())).ReturnsAsync(4);
        _deviceEventRepo.Setup(r => r.CountAsync(from, to, It.IsAny<CancellationToken>())).ReturnsAsync(8);
        _tempBasalRepo.Setup(r => r.CountAsync(from, to, It.IsAny<CancellationToken>())).ReturnsAsync(16);
        _bolusCalcRepo.Setup(r => r.CountAsync(from, to, It.IsAny<CancellationToken>())).ReturnsAsync(32);
        _dbContext.CarbIntakes.AddRange(Enumerable.Range(0, 2).Select(i => CarbIntake(from.AddMinutes(i), null)));
        _dbContext.ProfileActivations.AddRange(
            Enumerable.Range(0, 64).Select(i => Activation($"ps-{i}", from.AddMinutes(i)))
                .Append(Activation(null, from.AddHours(2)))
                .Append(Activation("ps-late", to.AddHours(1))));
        _dbContext.Notes.AddRange(Enumerable.Range(0, 128).Select(i => new NoteEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = from.AddMinutes(i), Text = "apart",
        }));
        await _dbContext.SaveChangesAsync();

        var count = await _service.CountProjectedTreatmentsAsync(
            new DateTimeOffset(from).ToUnixTimeMilliseconds(), new DateTimeOffset(to).ToUnixTimeMilliseconds());

        count.Should().Be(255);
        _noteRepo.Verify(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);

        static ProfileActivationEntity Activation(string? legacyId, DateTime start) => new()
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = legacyId, DataSource = "loop", Timestamp = start,
            Phase = nameof(ProfileActivationPhase.Requested), Origin = nameof(ProfileActivationOrigin.Uploader),
            BaseProfileName = "Weekend",
        };
    }

    [Fact]
    public async Task CountProjectedTreatments_ABolusAndCarbIntakeOfOneMeal_AreOneTreatment()
    {
        var meals = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).ToList();
        _dbContext.Boluses.AddRange(meals.Select((id, i) => Bolus(Window.AddMinutes(i), id)));
        _dbContext.CarbIntakes.AddRange(meals.Select((id, i) => CarbIntake(Window.AddMinutes(i), id)));
        await _dbContext.SaveChangesAsync();

        await AssertCountMatchesTheListAsync(3);
    }

    [Fact]
    public async Task CountProjectedTreatments_ABolusAndACarbIntakeOfNoMeal_AreTwoTreatments()
    {
        _dbContext.Boluses.AddRange(
            Bolus(Window, null),
            Bolus(Window.AddMinutes(1), Guid.CreateVersion7()));
        _dbContext.CarbIntakes.AddRange(
            CarbIntake(Window.AddMinutes(2), null),
            CarbIntake(Window.AddMinutes(3), Guid.CreateVersion7()));
        await _dbContext.SaveChangesAsync();

        await AssertCountMatchesTheListAsync(4);
    }

    [Fact]
    public async Task CountProjectedTreatments_AnExtraCarbIntakeOfAMeal_IsServedOnItsOwn()
    {
        var meal = Guid.CreateVersion7();
        _dbContext.Boluses.Add(Bolus(Window, meal));
        _dbContext.CarbIntakes.AddRange(
            CarbIntake(Window, meal, carbs: 40),
            CarbIntake(Window.AddMinutes(5), meal, carbs: 10));
        await _dbContext.SaveChangesAsync();

        await AssertCountMatchesTheListAsync(2);
    }

    [Fact]
    public async Task CountProjectedTreatments_AnExtraBolusOfAMeal_IsServedOnItsOwn()
    {
        var meal = Guid.CreateVersion7();
        _dbContext.Boluses.AddRange(Bolus(Window, meal, insulin: 4), Bolus(Window.AddMinutes(5), meal, insulin: 1));
        _dbContext.CarbIntakes.Add(CarbIntake(Window, meal));
        await _dbContext.SaveChangesAsync();

        await AssertCountMatchesTheListAsync(2);
    }

    [Fact]
    public async Task CountProjectedTreatments_AMealWhoseCarbIntakeIsOutsideTheWindow_IsTheBolusAlone()
    {
        var meal = Guid.CreateVersion7();
        _dbContext.Boluses.Add(Bolus(Window.AddMinutes(30), meal));
        _dbContext.CarbIntakes.Add(CarbIntake(Window.AddMinutes(-30), meal));
        await _dbContext.SaveChangesAsync();

        await AssertCountMatchesTheListAsync(1);
    }

    [Fact]
    public async Task CountProjectedTreatments_AMealWhoseBolusIsOutsideTheWindow_IsTheCarbIntakeAlone()
    {
        var meal = Guid.CreateVersion7();
        _dbContext.Boluses.Add(Bolus(Window.AddMinutes(-30), meal));
        _dbContext.CarbIntakes.Add(CarbIntake(Window.AddMinutes(30), meal));
        await _dbContext.SaveChangesAsync();

        await AssertCountMatchesTheListAsync(1);
    }

    [Fact]
    public async Task CountProjectedTreatments_ADeletedCarbIntakeOfAMeal_LeavesTheBolusServed()
    {
        var meal = Guid.CreateVersion7();
        var deleted = CarbIntake(Window, meal);
        deleted.DeletedAt = Window;
        _dbContext.Boluses.Add(Bolus(Window, meal));
        _dbContext.CarbIntakes.Add(deleted);
        await _dbContext.SaveChangesAsync();

        await AssertCountMatchesTheListAsync(1);
    }

    /// <summary>
    /// Each instant is served as a read of it alone serves it: a meal pairs at one instant, a bolus
    /// and a carb intake of one correlation at two instants do not, and a record between them is not read.
    /// </summary>
    [Fact]
    public async Task GetProjectedTreatmentsAt_ServesEachInstantAsAReadOfItAlone()
    {
        var later = Window.AddDays(365);
        var (meal, split) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var mealBolus = Bolus(later, meal, insulin: 4);
        var splitBolus = Bolus(Window, split, insulin: 3);
        var splitCarbs = CarbIntake(later, split);
        var between = Bolus(Window.AddDays(100), correlationId: null);
        _dbContext.Boluses.AddRange(mealBolus, splitBolus, between);
        _dbContext.CarbIntakes.AddRange(CarbIntake(later, meal), splitCarbs);
        await _dbContext.SaveChangesAsync();

        var served = (await _service.GetProjectedTreatmentsAtAsync([Mills(Window), Mills(later)])).ToList();

        served.Select(t => t.Id).Should().BeEquivalentTo(
            [mealBolus.Id.ToString(), splitBolus.Id.ToString(), splitCarbs.Id.ToString()]);
        served.Single(t => t.Id == mealBolus.Id.ToString()).EventType.Should().Be(TreatmentTypes.MealBolus);
        served.Single(t => t.Id == splitBolus.Id.ToString()).Carbs.Should().BeNull();
        served.Single(t => t.Id == splitCarbs.Id.ToString()).Insulin.Should().BeNull();
    }

    /// <summary>
    /// Each instant is limited on its own, as a read of it alone is: a crowded instant keeps the
    /// records with the newest ids, and cannot take the rows of another instant in the same read.
    /// </summary>
    [Fact]
    public async Task GetProjectedTreatmentsAt_LimitsEachInstantOnItsOwnNewestIdFirst()
    {
        var (sparse, crowded) = (Window, Window.AddDays(1));
        var pileUp = Enumerable.Range(0, 250).Select(_ => Bolus(crowded, correlationId: null)).ToList();
        var alone = Bolus(sparse, correlationId: null);
        _dbContext.Boluses.AddRange([.. pileUp, alone]);
        await _dbContext.SaveChangesAsync();

        var both = await IdsServedAtAsync(Mills(sparse), Mills(crowded));
        var crowdedAlone = await IdsServedAtAsync(Mills(crowded));
        var sparseAlone = await IdsServedAtAsync(Mills(sparse));

        crowdedAlone.Should().BeEquivalentTo(pileUp.Select(b => b.Id).OrderDescending().Take(100).Select(id => id.ToString()));
        sparseAlone.Should().Equal(alone.Id.ToString());
        both.Should().BeEquivalentTo([.. crowdedAlone, .. sparseAlone]);
    }

    [Fact]
    public async Task GetProjectedTreatmentsAt_LeavesOutACopyDeduplicationDemoted()
    {
        var (primary, copy) = (Bolus(Window, correlationId: null), Bolus(Window, correlationId: null));
        _dbContext.Boluses.AddRange(primary, copy);
        _dbContext.LinkedRecords.Add(new LinkedRecordEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            CanonicalId = Guid.CreateVersion7(),
            RecordType = RecordTypeKeys.Key(RecordType.Bolus),
            RecordId = copy.Id,
            SourceTimestamp = Mills(Window),
            DataSource = "glooko",
            IsPrimary = false,
        });
        await _dbContext.SaveChangesAsync();

        (await IdsServedAtAsync(Mills(Window))).Should().Equal(primary.Id.ToString());
    }

    private async Task<List<string?>> IdsServedAtAsync(params long[] instants) =>
        [.. (await _service.GetProjectedTreatmentsAtAsync(instants)).Select(t => t.Id)];

    private static readonly DateTime Window = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static BolusEntity Bolus(DateTime at, Guid? correlationId, double insulin = 2) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = at, CorrelationId = correlationId, Insulin = insulin,
    };

    private static CarbIntakeEntity CarbIntake(DateTime at, Guid? correlationId, double carbs = 30) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = at, CorrelationId = correlationId, Carbs = carbs,
    };

    /// <summary>
    /// Counts the window from 12:00 to 13:00 and lists it from the same rows, so a count that
    /// disagrees with what the list serves fails whichever of the two moved.
    /// </summary>
    private async Task AssertCountMatchesTheListAsync(long expected)
    {
        var (from, to) = (Window, Window.AddHours(1));
        _bolusRepo.As<IV4Repository<Bolus>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _dbContext.Boluses
                .Where(b => b.Timestamp >= from && b.Timestamp <= to).AsEnumerable().Select(BolusMapper.ToDomainModel).ToList());
        _carbIntakeRepo.As<IV4Repository<CarbIntake>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _dbContext.CarbIntakes
                .Where(c => c.Timestamp >= from && c.Timestamp <= to).AsEnumerable().Select(CarbIntakeMapper.ToDomainModel).ToList());
        _bolusRepo.Setup(r => r.CountAsync(from, to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _dbContext.Boluses.Count(b => b.Timestamp >= from && b.Timestamp <= to));
        _carbIntakeRepo.Setup(r => r.CountAsync(from, to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _dbContext.CarbIntakes.Count(c => c.Timestamp >= from && c.Timestamp <= to));

        var (fromMills, toMills) = (new DateTimeOffset(from).ToUnixTimeMilliseconds(), new DateTimeOffset(to).ToUnixTimeMilliseconds());
        var served = await _service.GetProjectedTreatmentsAsync(fromMills, toMills, 100);
        var counted = await _service.CountProjectedTreatmentsAsync(fromMills, toMills);

        served.Should().HaveCount((int)expected);
        counted.Should().Be(expected);
    }

    private V4ToLegacyProjectionService CreateService(NocturneDbContext dbContext) =>
        CreateService(dbContext.AsFactory());

    private V4ToLegacyProjectionService CreateService(INocturneDbContextFactory contexts) =>
        new(
            _bolusRepo.Object,
            _carbIntakeRepo.Object,
            _bgCheckRepo.Object,
            _noteRepo.Object,
            _deviceEventRepo.Object,
            _tempBasalRepo.Object,
            _bolusCalcRepo.Object,
            Mock.Of<IProfileActivationRepository>(),
            _treatmentFoodService.Object,
            contexts,
            NullLogger<V4ToLegacyProjectionService>.Instance
        );
}
