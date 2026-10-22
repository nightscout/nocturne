using Nocturne.API.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A treatment re-uploaded under its id with a different classification loses the rows its earlier
/// upload wrote to tables it no longer writes, and keeps every row it still writes.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentDecomposerReclassificationTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const long At = 1_780_000_000_000;

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TestNocturneDbContextFactory _factory;
    private readonly AuditContext _audit = new()
    {
        SubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
        SubjectName = "owner@example.com",
        AuthType = "SessionCookie",
    };
    private readonly IDeduplicationService _dedup = Mock.Of<IDeduplicationService>();
    private readonly Mock<IStateSpanService> _stateSpans = new();
    private readonly Mock<IProfileDecomposer> _profileDecomposer = new();

    public TreatmentDecomposerReclassificationTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
        _factory = new TestNocturneDbContextFactory(_context);

        _stateSpans
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpan span, CancellationToken _) =>
            {
                StoreSpan(span);
                return span;
            });
        _stateSpans
            .Setup(s => s.DeleteStateSpanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
            {
                var entity = _context.StateSpans.Single(s => s.OriginalId == id);
                entity.DeletedAt = DateTime.UtcNow;
                _context.SaveChanges();
                _context.Entry(entity).State = EntityState.Detached;
                return true;
            });
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private void StoreSpan(StateSpan span)
    {
        var entity = _context.StateSpans.SingleOrDefault(s => s.OriginalId == span.OriginalId);
        if (entity is null)
        {
            entity = StateSpanMapper.ToEntity(span);
            entity.Id = Guid.CreateVersion7();
            entity.TenantId = TenantId;
            _context.StateSpans.Add(entity);
        }
        else
        {
            StateSpanMapper.UpdateEntity(entity, span);
        }

        _context.SaveChanges();
        _context.Entry(entity).State = EntityState.Detached;
    }

    private TreatmentDecomposer Decomposer()
    {
        var foods = new Mock<ITreatmentFoodService>();
        foods
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return new TreatmentDecomposer(
            _factory,
            new BolusRepository(_factory, _dedup, _audit, NullLogger<BolusRepository>.Instance),
            new TempBasalRepository(_factory, _dedup, _audit, NullLogger<TempBasalRepository>.Instance),
            new CarbIntakeRepository(_factory, _dedup, _audit, NullLogger<CarbIntakeRepository>.Instance),
            new BGCheckRepository(_factory, _dedup, _audit, NullLogger<BGCheckRepository>.Instance),
            new NoteRepository(_factory, _dedup, _audit, NullLogger<NoteRepository>.Instance),
            new DeviceEventRepository(_factory, _dedup, _audit, NullLogger<DeviceEventRepository>.Instance),
            new BolusCalculationRepository(_factory, _dedup, _audit, NullLogger<BolusCalculationRepository>.Instance),
            _stateSpans.Object,
            ProfileActivationTestDeps.Repository(_context),
            ProfileActivationTestDeps.Writer(_context),
            foods.Object,
            Mock.Of<IDeviceService>(),
            Mock.Of<IPatientDeviceStamper>(),
            _profileDecomposer.Object,
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(),
            TempBasalTestDeps.Unscheduled(),
            _audit,
            NullLogger<TreatmentDecomposer>.Instance);
    }

    private Task DecomposeAsync(Treatment treatment, bool batch) => batch
        ? Decomposer().DecomposeBatchAsync([treatment], WriteOrigin.Live)
        : Decomposer().DecomposeAsync(treatment, WriteOrigin.Live);

    private static Treatment PlainNote() => new()
    {
        Id = "aaps-1", EventType = "Note", Mills = At, Notes = "Rhys (110%,1h)", EnteredBy = "openaps://AndroidAPS",
    };

    private static Treatment EffectiveSwitch() => new()
    {
        Id = "aaps-1",
        EventType = "Note",
        Mills = At,
        Notes = "Rhys (110%,1h)",
        EnteredBy = "openaps://AndroidAPS",
        ProfileJson = """{"units":"mmol","dia":5,"basal":[{"time":"00:00","timeAsSeconds":0,"value":0.88}]}""",
        OriginalProfileName = "Rhys",
        OriginalCustomizedName = "Rhys (110%,1h)",
        OriginalTimeshift = 3_600_000,
        OriginalPercentage = 110,
        OriginalDuration = 0,
        OriginalEnd = At,
    };

    private static Treatment BolusWizard(double? carbs, string? notes = null) => new()
    {
        Id = "wizard-1",
        EventType = "Bolus Wizard",
        Mills = At,
        Insulin = 4,
        Carbs = carbs,
        Notes = notes,
        BolusCalculatorResult = """{"carbs":40,"totalInsulin":4}""",
    };

    private async Task<int> DeletedRowsAsync() =>
        await _context.Boluses.IgnoreQueryFilters().CountAsync(e => e.DeletedAt != null)
        + await _context.CarbIntakes.IgnoreQueryFilters().CountAsync(e => e.DeletedAt != null)
        + await _context.Notes.IgnoreQueryFilters().CountAsync(e => e.DeletedAt != null)
        + await _context.BolusCalculations.IgnoreQueryFilters().CountAsync(e => e.DeletedAt != null)
        + await _context.StateSpans.IgnoreQueryFilters().CountAsync(e => e.DeletedAt != null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_note_reuploaded_as_an_effective_profile_switch_becomes_the_activation_alone(bool batch)
    {
        await DecomposeAsync(PlainNote(), batch);
        (await _context.Notes.CountAsync()).Should().Be(1);

        await DecomposeAsync(EffectiveSwitch(), batch);

        (await _context.Notes.CountAsync()).Should().Be(0, "the note it was first read as is no longer the treatment");
        var activation = await _context.ProfileActivations.AsNoTracking().SingleAsync(a => a.LegacyId == "aaps-1");
        activation.Phase.Should().Be(nameof(Nocturne.Core.Models.V4.ProfileActivationPhase.Effective));
        activation.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_removed_note_does_not_hold_off_a_later_upload_that_writes_it_again()
    {
        await DecomposeAsync(PlainNote(), batch: false);
        await DecomposeAsync(EffectiveSwitch(), batch: false);

        (await _context.GetBlockingLegacyIdsAsync<NoteEntity>(["aaps-1"])).Held.Should().BeEmpty(
            "a reclassification is not the user deleting the note");
    }

    [Fact]
    public async Task A_profile_switch_reuploaded_as_a_note_loses_its_activation_and_snapshot()
    {
        await DecomposeAsync(new Treatment
        {
            Id = "ps-1", EventType = "Profile Switch", Mills = At, Profile = "Weekend", Duration = 0,
            ProfileJson = """{"units":"mmol","dia":5,"basal":[{"time":"00:00","timeAsSeconds":0,"value":0.9}]}""",
        }, batch: false);

        await DecomposeAsync(new Treatment { Id = "ps-1", EventType = "Note", Mills = At, Notes = "Weekend" }, batch: false);

        (await _context.ProfileActivations.CountAsync()).Should().Be(0);
        (await _context.ProfileActivations.IgnoreQueryFilters().SingleAsync())
            .Should().Match<ProfileActivationEntity>(a => a.LegacyId == "ps-1" && a.DeletedAt != null);
        (await _context.GetBlockingLegacyIdsAsync<ProfileActivationEntity>(["ps-1"])).Held.Should().BeEmpty(
            "a reclassification is not the user deleting the switch");
        _profileDecomposer.Verify(d => d.DeleteByLegacyIdAsync("ps-1", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
        (await _context.Notes.SingleAsync()).Text.Should().Be("Weekend");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_bolus_wizard_edited_to_drop_its_carbs_loses_only_the_carb_half(bool batch)
    {
        await DecomposeAsync(BolusWizard(carbs: 40), batch);
        (await _context.CarbIntakes.CountAsync()).Should().Be(1);

        await DecomposeAsync(BolusWizard(carbs: null), batch);

        (await _context.CarbIntakes.CountAsync()).Should().Be(0);
        (await _context.Boluses.SingleAsync()).Insulin.Should().Be(4);
        (await _context.BolusCalculations.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reupload_of_the_same_kind_removes_nothing(bool batch)
    {
        await DecomposeAsync(BolusWizard(carbs: 40, notes: "lunch"), batch);
        await DecomposeAsync(BolusWizard(carbs: 40, notes: "lunch"), batch);

        (await DeletedRowsAsync()).Should().Be(0);
        (await _context.Boluses.CountAsync()).Should().Be(1);
        (await _context.CarbIntakes.CountAsync()).Should().Be(1);
        (await _context.BolusCalculations.CountAsync()).Should().Be(1);
        (await _context.Notes.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_reupload_of_an_effective_profile_switch_removes_nothing()
    {
        await DecomposeAsync(EffectiveSwitch(), batch: false);
        await DecomposeAsync(EffectiveSwitch(), batch: false);

        (await DeletedRowsAsync()).Should().Be(0);
        _stateSpans.Verify(s => s.DeleteStateSpanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_meal_resent_as_one_half_drops_the_stored_twin()
    {
        // A meal stored before meals were decomposed by content: a 0 U bolus beside the carbs
        _context.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = "meal-pre-fix",
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(At).UtcDateTime, Insulin = 0,
            CorrelationId = Guid.CreateVersion7(),
        });
        _context.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = "meal-pre-fix",
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(At).UtcDateTime, Carbs = 60,
            CorrelationId = Guid.CreateVersion7(),
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await Decomposer().DecomposeAsync(
            new Treatment { Id = "meal-pre-fix", EventType = "Meal Bolus", Mills = At, Carbs = 60 }, WriteOrigin.Live);

        result.UpdatedRecords.OfType<Nocturne.Core.Models.V4.CarbIntake>().Should().ContainSingle().Which.Carbs.Should().Be(60);
        result.CreatedRecords.Should().BeEmpty();
        _context.ChangeTracker.Clear();
        _context.Boluses.IgnoreQueryFilters().AsNoTracking().Where(b => b.LegacyId == "meal-pre-fix")
            .Should().ContainSingle().Which.DeletedAt.Should().NotBeNull("the zero-unit twin is no longer part of the record");
    }

    [Fact]
    public async Task A_meal_under_a_temp_basals_id_leaves_the_temp_basal()
    {
        _context.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = "syn-temp-basal",
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(At).UtcDateTime, Rate = 1.25, Origin = "Algorithm",
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await DecomposeAsync(
            new Treatment { Id = "syn-temp-basal", EventType = "Meal Bolus", Mills = At, Insulin = 0.0014756944 },
            batch: true);

        (await _context.TempBasals.CountAsync()).Should().Be(1);
    }
}
