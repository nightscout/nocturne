using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.ProfileActivations;

/// <summary>
/// The profile switches a person records in Nocturne, through <see cref="NativeProfileSwitches"/> over
/// the real repository, <see cref="ProfileActivationWriter"/> and <see cref="ProfileActivationResolver"/>.
/// </summary>
public sealed class NativeProfileSwitchesTests : IDisposable
{
    private static readonly Guid TenantId = TestTenantCache.DefaultTenantId;
    private static readonly DateTime T0 = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
    private const string Person = "Rhys";

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, SqliteNpgsqlJson.Translate);
    private readonly NocturneDbContext _context;
    private readonly ProfileActivationRepository _repository;
    private readonly ProfileActivationResolver _resolver;
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly FakeTimeProvider _time = new(T0);
    private readonly NativeProfileSwitches _native;

    private List<ScheduleEntry> _storedBasal = [new() { Time = "00:00", TimeAsSeconds = 0, Value = 0.8 }];
    private static readonly List<TargetRangeEntry> RhysTargets = [new() { Time = "00:00", TimeAsSeconds = 0, Low = 90, High = 110 }];

    public NativeProfileSwitchesTests()
    {
        _context = _db.CreateContext(TenantId);
        _repository = ProfileActivationTestDeps.Repository(_context);
        var cache = TestTenantCache.Create(_memory);

        var therapy = new Mock<ITherapySettingsRepository>();
        therapy.Setup(r => r.GetActiveAtAsync("Rhys", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TherapySettings { ProfileName = "Rhys", Units = "mmol/L", Dia = 5, CarbsHr = 30, Timezone = "Australia/Sydney" });
        therapy.Setup(r => r.GetByProfileNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var basal = new Mock<IBasalScheduleRepository>();
        basal.Setup(r => r.GetActiveAtAsync("Rhys", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BasalSchedule { ProfileName = "Rhys", Entries = _storedBasal });
        var carbRatio = new Mock<ICarbRatioScheduleRepository>();
        carbRatio.Setup(r => r.GetActiveAtAsync("Rhys", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CarbRatioSchedule { ProfileName = "Rhys", Entries = [new() { Time = "00:00", Value = 10 }] });
        var sensitivity = new Mock<ISensitivityScheduleRepository>();
        sensitivity.Setup(r => r.GetActiveAtAsync("Rhys", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensitivitySchedule { ProfileName = "Rhys", Entries = [new() { Time = "00:00", Value = 54 }] });
        var targets = new Mock<ITargetRangeScheduleRepository>();
        targets.Setup(r => r.GetActiveAtAsync("Rhys", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetRangeSchedule { ProfileName = "Rhys", Entries = RhysTargets });
        var audit = new Mock<IAuditContext>();
        audit.SetupGet(a => a.SubjectName).Returns(Person);

        _resolver = new ProfileActivationResolver(_repository, new ProfileStoreTimelineSource(therapy.Object), cache);
        _native = new NativeProfileSwitches(
            _repository, new ProfileActivationWriter(_repository, cache, TempBasalTestDeps.NoReresolution()), _resolver, therapy.Object, basal.Object,
            carbRatio.Object, sensitivity.Object, targets.Object, audit.Object, _time);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        _memory.Dispose();
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
        var result = await _native.CreateAsync(request, default);
        result.Outcome.Should().Be(NativeProfileSwitchOutcome.Written);
        return result.Activation!;
    }

    private async Task<ProfileActivation> ReadAsync(Guid id) =>
        (await _repository.GetIncludingDeletedAsync([id])).Single();

    private async Task<long?> EndOfAsync(Guid id) => ProfileActivationSelection.EndOf(await ReadAsync(id));

    private async Task<Guid?> AppliesAtAsync(DateTime at) =>
        (await _resolver.GetAtAsync(new DateTimeOffset(at).ToUnixTimeMilliseconds()))?.Activation.Id;

    private static long Mills(DateTime at) => new DateTimeOffset(at).ToUnixTimeMilliseconds();

    private async Task<ProfileActivation> StoreAsync(
        ProfileActivationOrigin origin,
        ProfileActivationPhase phase = ProfileActivationPhase.Requested,
        string? legacyId = null,
        string? dataSource = null)
    {
        var activation = new ProfileActivation
        {
            StartTimestamp = T0.AddHours(-1),
            Phase = phase,
            Origin = origin,
            BaseProfileName = "Rhys",
            DataSource = dataSource,
            EnteredBy = origin == ProfileActivationOrigin.User ? "Rhys" : "openaps://AndroidAPS",
            LegacyId = legacyId,
        };
        return await _repository.CreateAsync(activation, WriteOrigin.Live);
    }

    [Fact]
    public async Task ASwitchCapturesTheBaseProfileAsItStandsAndIsRecordedAsThePersons()
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
            EnteredBy = Person,
            BaseProfileName = "Rhys",
            Percentage = 110,
            TimeshiftMs = -2 * 3_600_000L,
            RequestedDurationMs = (long?)90 * 60_000,
            SnapshotIsCustomized = false,
        });
        stored.Snapshot!.Basal.Should().BeEquivalentTo(_storedBasal);
        stored.Snapshot.Targets.Should().BeEquivalentTo(RhysTargets);
        stored.Snapshot.Should().BeEquivalentTo(new { Units = "mmol/L", Dia = 5.0, CarbsHr = 30, Timezone = "Australia/Sydney" });
        stored.Snapshot.HasEverySchedule.Should().BeTrue();
        ProfileActivationSource.IsNative(stored).Should().BeTrue();
    }

    [Fact]
    public async Task ASwitchIsNeverServedAsALegacyTreatment()
    {
        await CreateAsync(Request());
        await StoreAsync(ProfileActivationOrigin.Uploader, legacyId: "6500000000000000000000a1");

        var served = await LegacyProfileActivationTable.Rows(_context).ToListAsync();

        served.Should().ContainSingle().Which.Origin.Should().Be(nameof(ProfileActivationOrigin.Uploader),
            "a recorded switch stays off the treatments feed; see NativeProfileSwitches");
    }

    [Theory]
    [InlineData("Holiday")]
    [InlineData("Rhys@@@@@1700000000000")]
    public async Task AProfileNotInTheStoreIsRefused(string name)
    {
        var result = await _native.CreateAsync(new CreateProfileActivationRequest { BaseProfileName = name }, default);

        result.Outcome.Should().Be(NativeProfileSwitchOutcome.ProfileNotFound);
        result.Activation.Should().BeNull();
        (await _context.ProfileActivations.CountAsync()).Should().Be(0);
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
    public async Task ASecondSubmitInTheSameMillisecondSupersedesTheFirst()
    {
        var first = await CreateAsync(Request(percentage: 110));
        var second = await CreateAsync(Request(percentage: 120));

        second.StartTimestamp.Should().Be(T0.AddMilliseconds(1), "a tie on start would leave the id to pick");
        (await ReadAsync(first.Id)).EndTimestamp.Should().Be(T0.AddMilliseconds(1));
        (await AppliesAtAsync(T0.AddMinutes(1))).Should().Be(second.Id);
    }

    [Fact]
    public async Task EndingATemporarySwitchLeavesThePermanentOneUnderItRunning()
    {
        var permanent = await CreateAsync(Request());
        _time.Advance(TimeSpan.FromHours(1));
        var temporary = await CreateAsync(Request(percentage: 150, minutes: 120));
        _time.Advance(TimeSpan.FromMinutes(30));

        var result = await _native.EndAsync(temporary.Id, default);

        result.Outcome.Should().Be(NativeProfileSwitchOutcome.Written);
        (await ReadAsync(temporary.Id)).Should().BeEquivalentTo(new
        {
            EndTimestamp = (DateTime?)T0.AddMinutes(90),
            EndedAt = (DateTime?)T0.AddMinutes(90),
            EndedBy = Person,
        });
        (await EndOfAsync(permanent.Id)).Should().BeNull();
        (await AppliesAtAsync(T0.AddMinutes(100))).Should().Be(permanent.Id);
    }

    [Fact]
    public async Task EndingAPermanentSwitchResumesThePermanentOneItReplaced()
    {
        var earlier = await CreateAsync(Request());
        _time.Advance(TimeSpan.FromHours(1));
        var later = await CreateAsync(Request(percentage: 120));
        (await ReadAsync(earlier.Id)).EndTimestamp.Should().Be(T0.AddHours(1));
        _time.Advance(TimeSpan.FromHours(1));

        await _native.EndAsync(later.Id, default);

        (await ReadAsync(later.Id)).EndTimestamp.Should().Be(T0.AddHours(2));
        (await EndOfAsync(earlier.Id)).Should().BeNull("an ended switch is bounded, so it leaves the earlier one running");
        (await AppliesAtAsync(T0.AddMinutes(90))).Should().Be(later.Id);
        (await AppliesAtAsync(T0.AddHours(3))).Should().Be(earlier.Id);
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

    [Fact]
    public async Task ACareportalSwitchIsTheUploadersToEndOrDelete()
    {
        var careportal = await StoreAsync(ProfileActivationOrigin.User, legacyId: "6500000000000000000000b1");

        (await _native.EndAsync(careportal.Id, default)).Outcome.Should().Be(NativeProfileSwitchOutcome.Uploaded);
        (await _native.DeleteAsync(careportal.Id, default)).Should().Be(NativeProfileSwitchOutcome.Uploaded);
        (await _native.RenewAsync(careportal.Id, default)).Outcome.Should().Be(NativeProfileSwitchOutcome.Uploaded);
        (await ReadAsync(careportal.Id)).EndedAt.Should().BeNull();
    }

    [Fact]
    public async Task AConnectorsActivationIsItsOwnToEndOrDelete()
    {
        var reported = await StoreAsync(ProfileActivationOrigin.Connector, ProfileActivationPhase.Effective,
            dataSource: DataSources.GlookoConnector);

        (await _native.EndAsync(reported.Id, default)).Outcome.Should().Be(NativeProfileSwitchOutcome.OwnedBySource);
        (await _native.DeleteAsync(reported.Id, default)).Should().Be(NativeProfileSwitchOutcome.OwnedBySource);
        (await _repository.GetByIdAsync(reported.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task WhileAnAidSystemReportsItsPumpARecordedSwitchIsKeptButDoesNotApply()
    {
        var reported = await StoreAsync(ProfileActivationOrigin.Uploader, ProfileActivationPhase.Effective,
            legacyId: "6500000000000000000000c1");
        var recorded = await CreateAsync(Request(percentage: 120));

        var status = await _native.GetStatusAsync(default);

        status.Active!.Id.Should().Be(reported.Id);
        status.Reported!.Id.Should().Be(reported.Id);
        status.Recorded!.Id.Should().Be(recorded.Id);
        status.RecordedSwitchesApply.Should().BeFalse();
    }

    [Fact]
    public async Task WithNothingReportingARecordedSwitchApplies()
    {
        var recorded = await CreateAsync(Request(percentage: 120));

        var status = await _native.GetStatusAsync(default);

        status.Active!.Id.Should().Be(recorded.Id);
        status.Reported.Should().BeNull();
        status.RecordedSwitchesApply.Should().BeTrue();
        status.RecordedProfileChanged.Should().BeFalse();
    }

    [Fact]
    public async Task AnEditToTheProfileLeavesTheSwitchOnItsSchedulesAndIsReported()
    {
        var recorded = await CreateAsync(Request(percentage: 110));
        _storedBasal = [new() { Time = "00:00", TimeAsSeconds = 0, Value = 1.2 }];
        _time.Advance(TimeSpan.FromMinutes(5));

        var resolved = (await _resolver.GetAtAsync(Mills(T0.AddMinutes(5))))!;
        resolved.Source.Should().Be(ProfileScheduleSource.BaseSnapshot);
        resolved.Schedules!.Basal!.Single().Value.Should().Be(0.8);
        (await _native.GetStatusAsync(default)).RecordedProfileChanged.Should().BeTrue();

        var renewed = (await _native.RenewAsync(recorded.Id, default)).Activation!;
        renewed.Snapshot!.Basal!.Single().Value.Should().Be(1.2);
        renewed.Percentage.Should().Be(110);
        renewed.IsTemporary.Should().BeFalse("a permanent switch stays permanent");
        (await ReadAsync(recorded.Id)).EndTimestamp.Should().Be(renewed.StartTimestamp);
        (await _native.GetStatusAsync(default)).RecordedProfileChanged.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchingAgainKeepsATemporarySwitchsEnd()
    {
        var recorded = await CreateAsync(Request(timeshift: 1, minutes: 120));
        _time.Advance(TimeSpan.FromMinutes(45));

        var renewed = (await _native.RenewAsync(recorded.Id, default)).Activation!;

        ProfileActivationSelection.EndOf(renewed).Should().Be(Mills(T0.AddMinutes(120)));
        renewed.TimeshiftMs.Should().Be(3_600_000);
        (await ReadAsync(recorded.Id)).EndTimestamp.Should().Be(T0.AddMinutes(45));
    }

    [Fact]
    public async Task AnUnknownIdIsNotFound()
    {
        (await _native.EndAsync(Guid.CreateVersion7(), default)).Outcome.Should().Be(NativeProfileSwitchOutcome.NotFound);
        (await _native.DeleteAsync(Guid.CreateVersion7(), default)).Should().Be(NativeProfileSwitchOutcome.NotFound);
        (await _native.RenewAsync(Guid.CreateVersion7(), default)).Outcome.Should().Be(NativeProfileSwitchOutcome.NotFound);
    }
}
