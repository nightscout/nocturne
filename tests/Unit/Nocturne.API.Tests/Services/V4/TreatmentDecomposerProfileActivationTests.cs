using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A Profile Switch decomposes into a requested <see cref="V4Models.ProfileActivation"/>, and AAPS's
/// effective profile switch (<see cref="EffectiveProfileSwitch"/>) into an effective one that confirms
/// it; each reads back as the treatment it was uploaded as.
/// </summary>
public class TreatmentDecomposerProfileActivationTests : IDisposable
{
    private const string AapsV1 = "openaps://AndroidAPS";
    private const long SwitchMills = 1700000000000;
    private const long ConfirmedMills = SwitchMills + 90_000;

    private const string EffectiveProfileJson =
        """{"units":"mmol","dia":5,"timezone":"Australia/Brisbane","sens":[{"time":"00:00","timeAsSeconds":0,"value":2.0}],"carbratio":[{"time":"00:00","timeAsSeconds":0,"value":9.1}],"basal":[{"time":"00:00","timeAsSeconds":0,"value":0.88}],"target_low":[{"time":"00:00","timeAsSeconds":0,"value":5.5}],"target_high":[{"time":"00:00","timeAsSeconds":0,"value":5.5}]}""";

    private const string BaseProfileJson =
        """{"units":"mg/dl","dia":4,"basal":[{"time":"00:00","value":0.8},{"time":"06:00","value":1.0}],"carbratio":[{"time":"00:00","value":10}],"sens":[{"time":"00:00","value":40}],"target_low":[{"time":"00:00","value":100}],"target_high":[{"time":"00:00","value":120}]}""";

    private readonly NocturneDbContext _context;
    private readonly Mock<IProfileDecomposer> _profileDecomposer = new();
    private readonly TreatmentDecomposer _decomposer;

    public TreatmentDecomposerProfileActivationTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = TestTenantCache.DefaultTenantId;
        var dedup = new Mock<IDeduplicationService>().Object;
        var audit = new Mock<IAuditContext>().Object;
        var factory = new TestNocturneDbContextFactory(_context);

        var deviceService = new Mock<IDeviceService>();
        deviceService
            .Setup(s => s.ResolveAsync(It.IsAny<V4Models.DeviceCategory>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        _decomposer = new TreatmentDecomposer(
            factory,
            new BolusRepository(factory, dedup, audit, NullLogger<BolusRepository>.Instance),
            new Mock<ITempBasalRepository>().Object,
            new CarbIntakeRepository(factory, dedup, audit, NullLogger<CarbIntakeRepository>.Instance),
            new BGCheckRepository(factory, dedup, audit, NullLogger<BGCheckRepository>.Instance),
            new NoteRepository(factory, dedup, audit, NullLogger<NoteRepository>.Instance),
            new DeviceEventRepository(factory, dedup, audit, NullLogger<DeviceEventRepository>.Instance),
            new BolusCalculationRepository(factory, dedup, audit, NullLogger<BolusCalculationRepository>.Instance),
            Mock.Of<IStateSpanService>(),
            ProfileActivationTestDeps.Repository(_context),
            ProfileActivationTestDeps.Writer(_context),
            new Mock<ITreatmentFoodService>().Object,
            deviceService.Object,
            Mock.Of<IPatientDeviceStamper>(),
            _profileDecomposer.Object,
            new Mock<IActiveProfileResolver>().Object,
            new Mock<IPatientInsulinRepository>().Object,
            TempBasalTestDeps.Unscheduled(),
            Mock.Of<IAuditContext>(),
            NullLogger<TreatmentDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static DateTime At(long mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;

    private static Treatment EffectiveSwitch(string? enteredBy = AapsV1) => new()
    {
        Id = "eps-1",
        EventType = "Note",
        Mills = ConfirmedMills,
        EnteredBy = enteredBy,
        Notes = "Rhys (110%,1h)",
        ProfileJson = EffectiveProfileJson,
        OriginalProfileName = "Rhys",
        OriginalCustomizedName = "Rhys (110%,1h)",
        OriginalTimeshift = 3_600_000,
        OriginalPercentage = 110,
        OriginalDuration = 3_600_000,
        OriginalEnd = SwitchMills + 3_600_000,
        UtcOffset = 600,
    };

    private static Treatment TemporarySwitch() => new()
    {
        Id = "ps-1",
        EventType = "Profile Switch",
        Mills = SwitchMills,
        EnteredBy = AapsV1,
        Profile = "Rhys (110%,1h)",
        OriginalProfileName = "Rhys",
        OriginalDuration = 3_600_000,
        Percentage = 110,
        Timeshift = 3_600_000,
        Duration = 60,
        ProfileJson = BaseProfileJson,
        UtcOffset = 600,
    };

    private static Treatment PermanentSwitch(string id, long mills, string profile) => new()
    {
        Id = id,
        EventType = "Profile Switch",
        Mills = mills,
        EnteredBy = "careportal",
        Profile = profile,
    };

    private Task<ProfileActivationEntity> StoredAsync(string legacyId) =>
        _context.ProfileActivations.AsNoTracking().IgnoreQueryFilters().SingleAsync(a => a.LegacyId == legacyId);

    private async Task<Treatment> ProjectAsync(string legacyId) =>
        LegacyTreatmentTables.ProjectProfileActivation(ProfileActivationMapper.ToDomainModel(await StoredAsync(legacyId)));

    [Fact]
    public async Task AProfileSwitchIsARequestedActivationOfTheProfileItCustomizes()
    {
        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);

        var stored = ProfileActivationMapper.ToDomainModel(await StoredAsync("ps-1"));
        stored.Phase.Should().Be(V4Models.ProfileActivationPhase.Requested);
        stored.Origin.Should().Be(V4Models.ProfileActivationOrigin.Uploader);
        stored.BaseProfileName.Should().Be("Rhys");
        stored.Percentage.Should().Be(110);
        stored.TimeshiftMs.Should().Be(3_600_000, "AAPS uploads the shift in milliseconds");
        stored.RequestedDurationMs.Should().Be(3_600_000);
        stored.StartTimestamp.Should().Be(At(SwitchMills));
        stored.EndTimestamp.Should().Be(At(SwitchMills + 3_600_000));
        stored.EnteredBy.Should().Be(AapsV1);
        stored.SnapshotIsCustomized.Should().BeFalse();
        stored.Snapshot!.Basal!.Select(e => e.Value).Should().Equal(0.8, 1.0);
        stored.Snapshot.Sensitivity!.Single().Value.Should().Be(40);
    }

    [Fact]
    public async Task AProfileSwitchWritesNoProfileSnapshotRows()
    {
        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);

        _profileDecomposer.Verify(
            d => d.DecomposeAsync(It.IsAny<Profile>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        (await _context.StateSpans.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AnEffectiveProfileSwitchIsAnEffectiveActivationNotANote()
    {
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);

        (await _context.Notes.CountAsync()).Should().Be(0);
        var stored = ProfileActivationMapper.ToDomainModel(await StoredAsync("eps-1"));
        stored.Phase.Should().Be(V4Models.ProfileActivationPhase.Effective);
        stored.BaseProfileName.Should().Be("Rhys");
        stored.Percentage.Should().Be(110);
        stored.TimeshiftMs.Should().Be(3_600_000);
        stored.RequestedDurationMs.Should().Be(3_600_000);
        stored.EndTimestamp.Should().Be(At(SwitchMills + 3_600_000), "a temporary effective switch ends where AAPS says it does");
        stored.SnapshotIsCustomized.Should().BeTrue();
        stored.Snapshot!.Basal!.Single().Value.Should().Be(0.88);
        stored.Snapshot.Sensitivity!.Single().Value.Should().Be(36, "mmol sensitivity is kept in mg/dL");
        stored.Snapshot.Timezone.Should().Be("Australia/Brisbane");
    }

    [Fact]
    public async Task APermanentEffectiveSwitchIsNotEndedByAnOriginalEndAtItsStart()
    {
        var treatment = EffectiveSwitch();
        treatment.OriginalDuration = 0;
        treatment.OriginalEnd = ConfirmedMills;

        await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live);

        var stored = await StoredAsync("eps-1");
        stored.EndTimestamp.Should().BeNull();
        stored.RequestedDurationMs.Should().BeNull();
    }

    [Fact]
    public async Task TheAapsInsulinConfigurationIsKeptOnTheSnapshot()
    {
        var treatment = EffectiveSwitch();
        treatment.AdditionalProperties = new()
        {
            ["icfg"] = JsonSerializer.SerializeToElement(new
            {
                insulinLabel = "Lyumjev",
                insulinEndTime = 25_200_000,
                insulinPeakTime = 2_700_000,
                concentration = 1.0,
            }),
        };

        await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live);

        var insulin = ProfileActivationMapper.ToDomainModel(await StoredAsync("eps-1")).Snapshot!.Insulin!;
        insulin.InsulinName.Should().Be("Lyumjev");
        insulin.Dia.Should().Be(7);
        insulin.Peak.Should().Be(45);
    }

    [Fact]
    public async Task AMalformedProfileJsonLeavesNoSnapshotAndStillWrites()
    {
        var treatment = TemporarySwitch();
        treatment.ProfileJson = "{not json";

        await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live);

        var stored = ProfileActivationMapper.ToDomainModel(await StoredAsync("ps-1"));
        stored.Snapshot.Should().BeNull();
        (await ProjectAsync("ps-1")).ProfileJson.Should().Be("{not json", "the upload is served back as it was sent");
    }

    [Fact]
    public async Task AV3EffectiveProfileSwitchIsAttributedToTheAppThatUploadedIt()
    {
        var treatment = EffectiveSwitch(enteredBy: null);
        treatment.AdditionalProperties = new() { ["app"] = JsonSerializer.SerializeToElement("AAPS") };

        await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live);

        var stored = await StoredAsync("eps-1");
        stored.App.Should().Be("AAPS");
        stored.Origin.Should().Be(nameof(V4Models.ProfileActivationOrigin.Uploader));
    }

    [Fact]
    public async Task ACareportalSwitchIsAUsersAndItsShiftIsHours()
    {
        await _decomposer.DecomposeAsync(new Treatment
        {
            Id = "ps-careportal",
            EventType = "Profile Switch",
            Mills = SwitchMills,
            EnteredBy = "careportal",
            Profile = "Weekend",
            Percentage = 90,
            Timeshift = 2,
            Duration = 30,
        }, WriteOrigin.Live);

        var stored = await StoredAsync("ps-careportal");
        stored.Origin.Should().Be(nameof(V4Models.ProfileActivationOrigin.User));
        stored.TimeshiftMs.Should().Be(7_200_000);
        var projected = await ProjectAsync("ps-careportal");
        projected.Timeshift.Should().Be(2);
        projected.DurationInMilliseconds.Should().Be(1_800_000, "a switch uploaded with minutes alone is served its length in both");
    }

    [Fact]
    public async Task ANoteWithoutAnOriginalProfileNameStaysANote()
    {
        await _decomposer.DecomposeAsync(new Treatment
        {
            Id = "note-1",
            EventType = "Note",
            Mills = SwitchMills,
            Notes = "Rhys",
            EnteredBy = AapsV1,
        }, WriteOrigin.Live);

        (await _context.ProfileActivations.CountAsync()).Should().Be(0);
        (await _context.Notes.SingleAsync()).Text.Should().Be("Rhys");
    }

    [Fact]
    public async Task TheBatchPathWritesTheSameActivation()
    {
        await _decomposer.DecomposeBatchAsync([EffectiveSwitch()], WriteOrigin.Live);

        (await _context.Notes.CountAsync()).Should().Be(0);
        (await StoredAsync("eps-1")).BaseProfileName.Should().Be("Rhys");
    }

    [Fact]
    public async Task AnEffectiveSwitchConfirmsAndEndsTheRequestedSwitchItRuns()
    {
        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);

        var requested = await StoredAsync("ps-1");
        var effective = await StoredAsync("eps-1");
        requested.EndTimestamp.Should().Be(At(ConfirmedMills));
        effective.RequestedActivationId.Should().Be(requested.Id);
        (await ProjectAsync("ps-1")).Duration.Should().Be(60, "the switch is still served with the duration it was uploaded with");
    }

    [Fact]
    public async Task ARequestedSwitchUploadedBehindItsEffectiveSwitchIsConfirmedAndEndedByIt()
    {
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);

        var requested = await StoredAsync("ps-1");
        requested.EndTimestamp.Should().Be(At(ConfirmedMills));
        (await StoredAsync("eps-1")).RequestedActivationId.Should().Be(requested.Id);
    }

    [Fact]
    public async Task SeveralEffectiveSwitchesMayConfirmOneRequestedSwitch()
    {
        var permanent = TemporarySwitch();
        permanent.Duration = 0;
        permanent.OriginalDuration = 0;
        await _decomposer.DecomposeAsync(permanent, WriteOrigin.Live);

        var first = EffectiveSwitch();
        first.OriginalDuration = 0;
        await _decomposer.DecomposeAsync(first, WriteOrigin.Live);
        var second = EffectiveSwitch();
        second.Id = "eps-2";
        second.Mills = ConfirmedMills + 7_200_000;
        second.OriginalDuration = 0;
        await _decomposer.DecomposeAsync(second, WriteOrigin.Live);

        var requested = await StoredAsync("ps-1");
        (await StoredAsync("eps-1")).RequestedActivationId.Should().Be(requested.Id);
        (await StoredAsync("eps-2")).RequestedActivationId.Should().Be(requested.Id);
        requested.EndTimestamp.Should().Be(At(ConfirmedMills), "it ends where the first confirmation starts");
        (await StoredAsync("eps-1")).EndTimestamp.Should().Be(At(ConfirmedMills + 7_200_000),
            "a permanent effective switch ends where the next permanent one of its source starts");
    }

    [Fact]
    public async Task AnEffectiveSwitchLeavesAnotherSourcesSwitchAlone()
    {
        var other = TemporarySwitch();
        other.EnteredBy = "careportal";
        await _decomposer.DecomposeAsync(other, WriteOrigin.Live);
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);

        var requested = await StoredAsync("ps-1");
        requested.EndTimestamp.Should().Be(At(SwitchMills + 3_600_000));
        (await StoredAsync("eps-1")).RequestedActivationId.Should().BeNull();
    }

    [Fact]
    public async Task APermanentSwitchEndsThePreviousPermanentSwitchOfItsSource()
    {
        await _decomposer.DecomposeAsync(PermanentSwitch("a", SwitchMills, "Weekday"), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(PermanentSwitch("b", SwitchMills + 3_600_000, "Weekend"), WriteOrigin.Live);

        (await StoredAsync("a")).EndTimestamp.Should().Be(At(SwitchMills + 3_600_000));
        (await StoredAsync("b")).EndTimestamp.Should().BeNull();
        (await ProjectAsync("a")).Duration.Should().Be(0, "a permanent switch a later one ended is not served as a temporary one");
    }

    [Fact]
    public async Task APermanentSwitchUploadedBehindALaterOneEndsAtIt()
    {
        await _decomposer.DecomposeAsync(PermanentSwitch("b", SwitchMills + 3_600_000, "Weekend"), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(PermanentSwitch("a", SwitchMills, "Weekday"), WriteOrigin.Live);

        (await StoredAsync("a")).EndTimestamp.Should().Be(At(SwitchMills + 3_600_000));
        (await StoredAsync("b")).EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task ATemporarySwitchEndsAtItsLengthAndLeavesThePermanentSwitchRunning()
    {
        await _decomposer.DecomposeAsync(PermanentSwitch("a", SwitchMills, "Weekday"), WriteOrigin.Live);
        var temporary = PermanentSwitch("t", SwitchMills + 3_600_000, "Sick");
        temporary.Duration = 30;
        await _decomposer.DecomposeAsync(temporary, WriteOrigin.Live);

        (await StoredAsync("t")).EndTimestamp.Should().Be(At(SwitchMills + 3_600_000 + 1_800_000));
        (await StoredAsync("a")).EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task ATemporarySwitchEndsEarlyAtTheNextSwitchOfItsSource()
    {
        var temporary = PermanentSwitch("t", SwitchMills, "Sick");
        temporary.Duration = 120;
        await _decomposer.DecomposeAsync(temporary, WriteOrigin.Live);
        await _decomposer.DecomposeAsync(PermanentSwitch("b", SwitchMills + 1_800_000, "Weekday"), WriteOrigin.Live);

        (await StoredAsync("t")).EndTimestamp.Should().Be(At(SwitchMills + 1_800_000));
        (await ProjectAsync("t")).Duration.Should().Be(120, "the switch is served with the length it asked for");
    }

    [Fact]
    public async Task AnEffectiveSwitchReadsBackAsTheNoteItWasUploadedAs()
    {
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);

        var stored = await StoredAsync("eps-1");
        var projected = await ProjectAsync("eps-1");

        projected.Id.Should().Be(stored.Id.ToString());
        projected.EventType.Should().Be("Note");
        projected.Mills.Should().Be(ConfirmedMills);
        projected.Notes.Should().Be("Rhys (110%,1h)");
        projected.EnteredBy.Should().Be(AapsV1);
        projected.ProfileJson.Should().Be(EffectiveProfileJson);
        projected.OriginalProfileName.Should().Be("Rhys");
        projected.OriginalCustomizedName.Should().Be("Rhys (110%,1h)");
        projected.OriginalTimeshift.Should().Be(3_600_000);
        projected.OriginalPercentage.Should().Be(110);
        projected.OriginalDuration.Should().Be(3_600_000);
        projected.OriginalEnd.Should().Be(SwitchMills + 3_600_000);
        projected.UtcOffset.Should().Be(600);
        projected.Profile.Should().BeNull();
        projected.Percentage.Should().BeNull();
        projected.Timeshift.Should().BeNull();
    }

    [Fact]
    public async Task AnAapsProfileSwitchReadsBackWithItsCustomizedNameAndOriginalFields()
    {
        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);

        var projected = await ProjectAsync("ps-1");
        projected.EventType.Should().Be("Profile Switch");
        projected.Profile.Should().Be("Rhys (110%,1h)");
        projected.OriginalProfileName.Should().Be("Rhys");
        projected.OriginalDuration.Should().Be(3_600_000);
        projected.Duration.Should().Be(60);
        projected.Percentage.Should().Be(110);
        projected.Timeshift.Should().Be(3_600_000, "it is served in the unit it was uploaded in");
        projected.ProfileJson.Should().Be(BaseProfileJson);
    }

    [Fact]
    public async Task AnAapsV3ProfileSwitchRoundTripsItsShiftLengthAndPumpRecord()
    {
        var treatment = TemporarySwitch();
        treatment.EnteredBy = null;
        treatment.Timeshift = -7_200_000;
        treatment.DurationInMilliseconds = 3_600_000;
        treatment.PumpId = 4102;
        treatment.PumpType = "OMNIPOD_DASH";
        treatment.PumpSerial = "33013206";
        treatment.AdditionalProperties = new() { ["app"] = JsonSerializer.SerializeToElement("AAPS") };

        await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live);

        (await StoredAsync("ps-1")).TimeshiftMs.Should().Be(-7_200_000);
        var projected = await ProjectAsync("ps-1");
        projected.Timeshift.Should().Be(-7_200_000);
        projected.Duration.Should().Be(60);
        projected.DurationInMilliseconds.Should().Be(3_600_000);
        projected.OriginalDuration.Should().Be(3_600_000);
        projected.PumpId.Should().Be(4102);
        projected.PumpType.Should().Be("OMNIPOD_DASH");
        projected.PumpSerial.Should().Be("33013206");
        projected.AdditionalProperties.Should().ContainKey("app").WhoseValue.Should().Be("AAPS");
    }

    [Fact]
    public async Task AnEffectiveSwitchMissingOriginalFieldsIsServedEveryOneAapsRequires()
    {
        var treatment = EffectiveSwitch();
        treatment.OriginalCustomizedName = null;
        treatment.OriginalTimeshift = null;
        treatment.OriginalPercentage = null;
        treatment.OriginalDuration = null;
        treatment.OriginalEnd = null;

        await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live);

        var projected = await ProjectAsync("eps-1");
        projected.ProfileJson.Should().Be(EffectiveProfileJson);
        projected.OriginalProfileName.Should().Be("Rhys");
        projected.OriginalCustomizedName.Should().Be("Rhys");
        projected.OriginalTimeshift.Should().Be(0);
        projected.OriginalPercentage.Should().Be(100);
        projected.OriginalDuration.Should().Be(0);
        projected.OriginalEnd.Should().Be(ConfirmedMills);
    }

    [Fact]
    public async Task AnEffectiveSwitchKeepsItsPumpRecord()
    {
        var treatment = EffectiveSwitch();
        treatment.PumpId = 77;
        treatment.PumpType = "OMNIPOD_DASH";
        treatment.PumpSerial = "33013206";

        await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live);

        var projected = await ProjectAsync("eps-1");
        projected.PumpId.Should().Be(77);
        projected.PumpType.Should().Be("OMNIPOD_DASH");
        projected.PumpSerial.Should().Be("33013206");
    }

    [Fact]
    public async Task AReuploadUpdatesTheActivationInPlace()
    {
        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);
        var first = await StoredAsync("ps-1");

        var edited = TemporarySwitch();
        edited.Percentage = 120;
        await _decomposer.DecomposeAsync(edited, WriteOrigin.Live);

        var second = await StoredAsync("ps-1");
        second.Id.Should().Be(first.Id, "the served identifier does not move");
        second.Percentage.Should().Be(120);
    }

    [Fact]
    public async Task AnAapsV1ProfileSwitchShiftIsStoredAndServedInMilliseconds()
    {
        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);

        var stored = ProfileActivationMapper.ToDomainModel(await StoredAsync("ps-1"));
        stored.TimeshiftMs.Should().Be(3_600_000);
        stored.AdditionalProperties.Should().ContainKey(ProfileSpanMetadata.TimeshiftUnitKey)
            .WhoseValue.Should().Match(v => v.ToString() == "ms");
        (await ProjectAsync("ps-1")).Timeshift.Should().Be(3_600_000, "AAPS reads the shift back in the unit it sent");
    }

    [Fact]
    public async Task AnIdenticalResendWritesNoRowVersion()
    {
        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);
        var requested = (await StoredAsync("ps-1")).SysUpdatedAt;
        var effective = (await StoredAsync("eps-1")).SysUpdatedAt;
        _context.ChangeTracker.Clear();

        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);

        (await StoredAsync("ps-1")).SysUpdatedAt.Should().Be(requested, "its settled end and link are written with it, not after");
        (await StoredAsync("eps-1")).SysUpdatedAt.Should().Be(effective);
    }

    private static Treatment AapsPermanentSwitch(string id, long mills, string profile)
    {
        var treatment = TemporarySwitch();
        treatment.Id = id;
        treatment.Mills = mills;
        treatment.Profile = profile;
        treatment.OriginalProfileName = profile;
        treatment.Percentage = 100;
        treatment.Timeshift = 0;
        treatment.Duration = 0;
        treatment.OriginalDuration = 0;
        return treatment;
    }

    private static Treatment AapsPermanentEffectiveSwitch(string id, long mills, string profile)
    {
        var treatment = EffectiveSwitch();
        treatment.Id = id;
        treatment.Mills = mills;
        treatment.OriginalProfileName = profile;
        treatment.OriginalCustomizedName = profile;
        treatment.Notes = profile;
        treatment.OriginalPercentage = 100;
        treatment.OriginalTimeshift = 0;
        treatment.OriginalDuration = 0;
        treatment.OriginalEnd = null;
        return treatment;
    }

    [Fact]
    public async Task AnEffectiveSwitchAfterATemporaryRequestRanOutConfirmsThePermanentRequestBeforeIt()
    {
        await _decomposer.DecomposeAsync(AapsPermanentSwitch("ps-0", SwitchMills - 3_600_000, "Rhys"), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(
            AapsPermanentEffectiveSwitch("eps-back", SwitchMills + 2 * 3_600_000, "Rhys"), WriteOrigin.Live);

        (await StoredAsync("eps-back")).RequestedActivationId.Should().Be((await StoredAsync("ps-0")).Id,
            "AAPS records the switch back to the permanent profile when the temporary one runs out");
    }

    [Fact]
    public async Task AnEffectiveSwitchConfirmsNoRequestOlderThanTheNewestStillRunning()
    {
        await _decomposer.DecomposeAsync(AapsPermanentSwitch("ps-0", SwitchMills - 3_600_000, "Rhys"), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(AapsPermanentSwitch("ps-other", SwitchMills, "Other"), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(
            AapsPermanentEffectiveSwitch("eps-late", SwitchMills + 3_600_000, "Rhys"), WriteOrigin.Live);

        (await StoredAsync("eps-late")).RequestedActivationId.Should().BeNull(
            "the newest request is Other's, so this one confirms nothing however far back Rhys was asked for");
    }

    [Fact]
    public async Task ADeleteBlocksTheSwitchComingBack()
    {
        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);
        var stored = await _context.ProfileActivations.SingleAsync(a => a.LegacyId == "ps-1");
        stored.DeletedAt = DateTime.UtcNow;
        _context.Entry(stored).Property<bool>("DeletedByUser").CurrentValue = true;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(1);
        (await _context.ProfileActivations.CountAsync()).Should().Be(0);
    }
}
