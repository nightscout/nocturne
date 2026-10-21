using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.ProfileActivations;

/// <summary>
/// The profile switches a person records in Nocturne, through <see cref="NativeProfileSwitches"/> over
/// the real repository and <see cref="ProfileActivationWriter"/>.
/// </summary>
public sealed class NativeProfileSwitchesTests : IDisposable
{
    private static readonly Guid TenantId = TestTenantCache.DefaultTenantId;
    private static readonly DateTime T0 = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, SqliteNpgsqlJson.Translate);
    private readonly NocturneDbContext _context;
    private readonly ProfileActivationRepository _repository;
    private readonly FakeTimeProvider _time = new(T0);
    private readonly Mock<IPatientDeviceRepository> _patientDevices = new();
    private readonly NativeProfileSwitches _native;

    private static readonly List<ScheduleEntry> RhysBasal = [new() { Time = "00:00", TimeAsSeconds = 0, Value = 0.8 }];
    private static readonly List<TargetRangeEntry> RhysTargets = [new() { Time = "00:00", TimeAsSeconds = 0, Low = 90, High = 110 }];

    public NativeProfileSwitchesTests()
    {
        _context = _db.CreateContext(TenantId);
        _repository = ProfileActivationTestDeps.Repository(_context);

        var therapy = new Mock<ITherapySettingsRepository>();
        therapy.Setup(r => r.GetActiveAtAsync("Rhys", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TherapySettings { ProfileName = "Rhys", Units = "mmol/L", Dia = 5, CarbsHr = 30, Timezone = "Australia/Sydney" });
        var basal = new Mock<IBasalScheduleRepository>();
        basal.Setup(r => r.GetActiveAtAsync("Rhys", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BasalSchedule { ProfileName = "Rhys", Entries = RhysBasal });
        var carbRatio = new Mock<ICarbRatioScheduleRepository>();
        carbRatio.Setup(r => r.GetActiveAtAsync("Rhys", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CarbRatioSchedule { ProfileName = "Rhys", Entries = [new() { Time = "00:00", Value = 10 }] });
        var sensitivity = new Mock<ISensitivityScheduleRepository>();
        sensitivity.Setup(r => r.GetActiveAtAsync("Rhys", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensitivitySchedule { ProfileName = "Rhys", Entries = [new() { Time = "00:00", Value = 54 }] });
        var targets = new Mock<ITargetRangeScheduleRepository>();
        targets.Setup(r => r.GetActiveAtAsync("Rhys", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetRangeSchedule { ProfileName = "Rhys", Entries = RhysTargets });

        _native = new NativeProfileSwitches(
            _repository, ProfileActivationTestDeps.Writer(_context), therapy.Object, basal.Object, carbRatio.Object,
            sensitivity.Object, targets.Object, _patientDevices.Object, _time);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private static CreateProfileActivationRequest Request(int percentage = 100, int timeshift = 0, int? minutes = null) => new()
    {
        BaseProfileName = "Rhys",
        Percentage = percentage,
        TimeshiftHours = timeshift,
        DurationMinutes = minutes,
    };

    private async Task<ProfileActivation> CreateAsync(CreateProfileActivationRequest request)
    {
        var (outcome, activation) = await _native.CreateAsync(request, default);
        outcome.Should().Be(NativeProfileSwitchOutcome.Written);
        return activation!;
    }

    private async Task<ProfileActivation> ReadAsync(Guid id) =>
        (await _repository.GetIncludingDeletedAsync([id])).Single();

    private async Task<ProfileActivation> StoreFromSourceAsync(ProfileActivationOrigin origin)
    {
        var activation = new ProfileActivation
        {
            StartTimestamp = T0.AddHours(-1),
            Phase = ProfileActivationPhase.Requested,
            Origin = origin,
            BaseProfileName = "Rhys",
            DataSource = origin == ProfileActivationOrigin.Connector ? DataSources.GlookoConnector : null,
            EnteredBy = "openaps://AndroidAPS",
            LegacyId = origin == ProfileActivationOrigin.Uploader ? "6500000000000000000000a1" : null,
        };
        return await _repository.CreateAsync(activation, WriteOrigin.Live);
    }

    [Fact]
    public async Task ASwitchCapturesTheBaseProfileAsItStandsAndIsRecordedAsAPersons()
    {
        var created = await CreateAsync(Request(percentage: 110, timeshift: -2, minutes: 90));

        var stored = await ReadAsync(created.Id);
        stored.Should().BeEquivalentTo(new
        {
            StartTimestamp = T0,
            EndTimestamp = (DateTime?)T0.AddMinutes(90),
            Phase = ProfileActivationPhase.Requested,
            Origin = ProfileActivationOrigin.User,
            DataSource = DataSources.ManualEntry,
            LegacyId = (string?)null,
            BaseProfileName = "Rhys",
            Percentage = 110,
            TimeshiftMs = -2 * 3_600_000L,
            RequestedDurationMs = (long?)90 * 60_000,
            SnapshotIsCustomized = false,
        });
        stored.Snapshot!.Basal.Should().BeEquivalentTo(RhysBasal);
        stored.Snapshot.Targets.Should().BeEquivalentTo(RhysTargets);
        stored.Snapshot.Should().BeEquivalentTo(new { Units = "mmol/L", Dia = 5.0, CarbsHr = 30, Timezone = "Australia/Sydney" });
        stored.Snapshot.HasEverySchedule.Should().BeTrue();
        ProfileActivationSource.IsNative(stored).Should().BeTrue();
    }

    [Fact]
    public async Task ASwitchIsNeverServedAsALegacyTreatment()
    {
        await CreateAsync(Request());
        await StoreFromSourceAsync(ProfileActivationOrigin.Uploader);

        var served = await LegacyProfileActivationTable.Rows(_context).ToListAsync();

        served.Should().ContainSingle().Which.Origin.Should().Be(nameof(ProfileActivationOrigin.Uploader),
            "AAPS imports any Profile Switch it reads on the treatments feed, so a recorded one stays off it");
    }

    [Theory]
    [InlineData("Holiday")]
    [InlineData("Rhys@@@@@1700000000000")]
    public async Task AProfileNotInTheStoreIsRefused(string name)
    {
        var (outcome, activation) = await _native.CreateAsync(new CreateProfileActivationRequest { BaseProfileName = name }, default);

        outcome.Should().Be(NativeProfileSwitchOutcome.ProfileNotFound);
        activation.Should().BeNull();
        (await _context.ProfileActivations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AnUnknownPatientDeviceIsRefusedAndAKnownOneIsStored()
    {
        var known = Guid.CreateVersion7();
        _context.PatientDevices.Add(new PatientDeviceEntity
        {
            Id = known,
            TenantId = TenantId,
            DeviceCategory = "InsulinPump",
            Manufacturer = "Tandem",
            Model = "t:slim X2",
        });
        await _context.SaveChangesAsync();
        _patientDevices.Setup(r => r.GetByIdAsync(known, It.IsAny<CancellationToken>())).ReturnsAsync(new PatientDevice { Id = known });

        var request = Request();
        request.PatientDeviceId = Guid.CreateVersion7();
        (await _native.CreateAsync(request, default)).Outcome.Should().Be(NativeProfileSwitchOutcome.PatientDeviceNotFound);

        request.PatientDeviceId = known;
        (await CreateAsync(request)).PatientDeviceId.Should().Be(known);
    }

    [Fact]
    public async Task ANewPermanentSwitchEndsThePreviousOne()
    {
        var first = await CreateAsync(Request());
        _time.Advance(TimeSpan.FromHours(1));
        var second = await CreateAsync(Request(percentage: 120));

        (await ReadAsync(first.Id)).EndTimestamp.Should().Be(T0.AddHours(1));
        (await ReadAsync(second.Id)).EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task EndingATemporarySwitchLeavesThePermanentOneUnderItRunning()
    {
        var permanent = await CreateAsync(Request());
        _time.Advance(TimeSpan.FromHours(1));
        var temporary = await CreateAsync(Request(percentage: 150, minutes: 120));
        _time.Advance(TimeSpan.FromMinutes(30));

        var (outcome, ended) = await _native.EndAsync(temporary.Id, default);

        outcome.Should().Be(NativeProfileSwitchOutcome.Written);
        ended!.EndTimestamp.Should().Be(T0.AddMinutes(90));
        (await ReadAsync(temporary.Id)).EndTimestamp.Should().Be(T0.AddMinutes(90));
        (await ReadAsync(permanent.Id)).EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task AnEndedSwitchKeepsItsEndWhenANeighbourIsResettled()
    {
        var temporary = await CreateAsync(Request(minutes: 120));
        _time.Advance(TimeSpan.FromMinutes(30));
        await _native.EndAsync(temporary.Id, default);
        var next = await CreateAsync(Request(percentage: 80, minutes: 60));

        (await _native.DeleteAsync(next.Id, default)).Should().Be(NativeProfileSwitchOutcome.Written);

        (await ReadAsync(temporary.Id)).EndTimestamp.Should().Be(T0.AddMinutes(30),
            "the person's end is part of the end it brings with it, not its requested two hours");
    }

    [Fact]
    public async Task AnEndedSwitchCannotBeEndedAgain()
    {
        var created = await CreateAsync(Request(minutes: 30));
        _time.Advance(TimeSpan.FromHours(1));

        (await _native.EndAsync(created.Id, default)).Outcome.Should().Be(NativeProfileSwitchOutcome.AlreadyEnded);
    }

    [Theory]
    [InlineData(ProfileActivationOrigin.Uploader)]
    [InlineData(ProfileActivationOrigin.Connector)]
    public async Task ASourcesActivationCannotBeEndedOrDeletedHere(ProfileActivationOrigin origin)
    {
        var stored = await StoreFromSourceAsync(origin);

        (await _native.EndAsync(stored.Id, default)).Outcome.Should().Be(NativeProfileSwitchOutcome.NotOwnedByUser);
        (await _native.DeleteAsync(stored.Id, default)).Should().Be(NativeProfileSwitchOutcome.NotOwnedByUser);
        (await ReadAsync(stored.Id)).EndTimestamp.Should().BeNull();
        (await _repository.GetByIdAsync(stored.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeletingASwitchLetsTheOneItEndedRunAgain()
    {
        var first = await CreateAsync(Request());
        _time.Advance(TimeSpan.FromHours(1));
        var second = await CreateAsync(Request(percentage: 120));

        (await _native.DeleteAsync(second.Id, default)).Should().Be(NativeProfileSwitchOutcome.Written);

        (await _repository.GetByIdAsync(second.Id)).Should().BeNull();
        (await ReadAsync(first.Id)).EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task AnUnknownIdIsNotFound()
    {
        (await _native.EndAsync(Guid.CreateVersion7(), default)).Outcome.Should().Be(NativeProfileSwitchOutcome.NotFound);
        (await _native.DeleteAsync(Guid.CreateVersion7(), default)).Should().Be(NativeProfileSwitchOutcome.NotFound);
    }
}
