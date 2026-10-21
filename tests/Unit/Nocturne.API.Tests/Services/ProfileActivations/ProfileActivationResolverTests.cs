using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.ProfileActivations;

/// <summary>
/// Which <see cref="ProfileActivation"/> applies at a time and where its schedules come from, through
/// <see cref="ProfileActivationResolver"/> and the point resolvers that read it.
/// </summary>
public sealed class ProfileActivationResolverTests : IDisposable
{
    private const long Hour = 3_600_000;
    private const long T0 = 1_705_320_000_000;

    private readonly List<ProfileActivation> _stored = [];
    private readonly Mock<IProfileActivationRepository> _repository = new();
    private readonly Mock<ITherapySettingsRepository> _therapy = new();
    private readonly Mock<IBasalScheduleRepository> _basal = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public ProfileActivationResolverTests()
    {
        _repository
            .Setup(r => r.GetOverlappingAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, int _, CancellationToken _) => _stored
                .Where(a => a.StartTimestamp <= to && (a.EndTimestamp is null || a.EndTimestamp > from))
                .OrderBy(a => a.StartTimestamp)
                .ToList());
        _repository
            .Setup(r => r.GetEffectiveSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _stored
                .Where(a => a.Phase == ProfileActivationPhase.Effective)
                .GroupBy(a => (a.DataSource, a.App, a.EnteredBy))
                .Select(g => (g.Key.DataSource, g.Key.App, g.Key.EnteredBy, g.Min(a => a.StartTimestamp)))
                .ToList());

        _therapy.Setup(r => r.GetByProfileNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _therapy.Setup(r => r.GetDefaultsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _therapy.Setup(r => r.GetByProfileNameAsync("Rhys", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TherapySettings { ProfileName = "Rhys", Timestamp = At(-24 * Hour) }]);
        _basal.Setup(r => r.GetByProfileNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _basal.Setup(r => r.GetByProfileNameAsync("Rhys", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Basal("Rhys", 1.0)]);
        _basal.Setup(r => r.GetByProfileNameAsync("Default", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Basal("Default", 0.5)]);
    }

    public void Dispose() => _cache.Dispose();

    private static DateTime At(long offset) => DateTimeOffset.FromUnixTimeMilliseconds(T0 + offset).UtcDateTime;

    private static BasalSchedule Basal(string profile, double rate) => new()
    {
        ProfileName = profile,
        Timestamp = At(-24 * Hour),
        Entries = [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = rate }],
    };

    private ProfileActivation Store(
        long start,
        string profile = "Rhys",
        ProfileActivationPhase phase = ProfileActivationPhase.Requested,
        long? durationMs = null,
        long? end = null,
        int percentage = 100,
        string? enteredBy = "careportal",
        ProfileActivationSnapshot? snapshot = null,
        bool customized = false)
    {
        var activation = new ProfileActivation
        {
            Id = Guid.CreateVersion7(),
            StartTimestamp = At(start),
            EndTimestamp = end is { } e ? At(e) : durationMs is { } d ? At(start + d) : null,
            RequestedDurationMs = durationMs,
            Phase = phase,
            BaseProfileName = profile,
            Percentage = percentage,
            EnteredBy = enteredBy,
            Snapshot = snapshot,
            SnapshotIsCustomized = customized,
        };
        _stored.Add(activation);
        return activation;
    }

    private ProfileActivationResolver Resolver(Nocturne.API.Services.Caching.ITenantCache? cache = null)
    {
        cache ??= TestTenantCache.Create(_cache);
        return new ProfileActivationResolver(_repository.Object, _therapy.Object, new ProfileVersionCache(cache), cache);
    }

    private BasalRateResolver BasalRates(IProfileActivationResolver resolver) => new(
        _basal.Object,
        _therapy.Object,
        Mock.Of<IPatientRecordRepository>(),
        new ActiveProfileResolver(resolver, _repository.Object, _therapy.Object),
        new ProfileVersionCache(TestTenantCache.Create(_cache)),
        NullLogger<BasalRateResolver>.Instance);

    private static ProfileActivationSnapshot Snapshot(double basal, bool everyKind = true) => new()
    {
        Basal = [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = basal }],
        CarbRatio = everyKind ? [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = 10 }] : null,
        Sensitivity = everyKind ? [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = 50 }] : null,
        Targets = everyKind ? [new TargetRangeEntry { Time = "00:00", TimeAsSeconds = 0, Low = 90, High = 110 }] : null,
    };

    [Fact]
    public async Task TheNewestStartWinsAmongTheActivationsRunning()
    {
        Store(0, "Weekday");
        var newer = Store(Hour, "Weekend");

        (await Resolver().GetAtAsync(T0 + 2 * Hour))!.Activation.Id.Should().Be(newer.Id);
    }

    [Fact]
    public async Task APermanentActivationAppliesUnderATemporaryOneAndAgainOnceItEnds()
    {
        var permanent = Store(0, "Weekday");
        var temporary = Store(Hour, "Sick", durationMs: Hour);

        var resolver = Resolver();
        (await resolver.GetAtAsync(T0 + Hour + Hour / 2))!.Activation.Id.Should().Be(temporary.Id);
        (await resolver.GetAtAsync(T0 + 3 * Hour))!.Activation.Id.Should().Be(permanent.Id);
    }

    [Fact]
    public async Task APermanentActivationStartedDuringATemporaryOneWins()
    {
        Store(0, "Sick", durationMs: 3 * Hour);
        var permanent = Store(Hour, "Weekday");

        var resolver = Resolver();
        (await resolver.GetAtAsync(T0 + 2 * Hour))!.Activation.Id.Should().Be(permanent.Id);
        (await resolver.GetAtAsync(T0 + 4 * Hour))!.Activation.Id.Should().Be(permanent.Id);
    }

    [Fact]
    public async Task ASourceThatReportsWhatItsPumpRunsIsReadOnlyFromThoseReports()
    {
        var effective = Store(0, "Rhys", ProfileActivationPhase.Effective, enteredBy: "openaps://AndroidAPS");
        Store(Hour, "Weekend", ProfileActivationPhase.Requested, enteredBy: "openaps://AndroidAPS");

        (await Resolver().GetAtAsync(T0 + 2 * Hour))!.Activation.Id.Should().Be(effective.Id,
            "a switch the pump has not confirmed is not what it runs");
    }

    [Fact]
    public async Task ASourcesRequestsFromBeforeItFirstReportedWhatItsPumpRunsStillApply()
    {
        var before = Store(0, "Weekday", enteredBy: "openaps://AndroidAPS");
        var sick = Store(Hour, "Sick", durationMs: Hour, enteredBy: "openaps://AndroidAPS");
        var effective = Store(10 * Hour, "Rhys", ProfileActivationPhase.Effective, enteredBy: "openaps://AndroidAPS");
        Store(11 * Hour, "Weekend", enteredBy: "openaps://AndroidAPS");

        var resolver = Resolver();
        (await resolver.GetAtAsync(T0 + Hour / 2))!.Activation.Id.Should().Be(before.Id);
        (await resolver.GetAtAsync(T0 + Hour + Hour / 2))!.Activation.Id.Should().Be(sick.Id);
        (await resolver.GetAtAsync(T0 + 5 * Hour))!.Activation.Id.Should().Be(before.Id,
            "the source reported nothing yet, so its requests are what it ran");
        (await resolver.GetAtAsync(T0 + 12 * Hour))!.Activation.Id.Should().Be(effective.Id,
            "a request after its first report waits to be reported");
    }

    [Fact]
    public async Task ASourceThatNeverReportsWhatItsPumpRunsAppliesItsRequestsAtOnce()
    {
        Store(0, "Rhys", ProfileActivationPhase.Effective, enteredBy: "openaps://AndroidAPS");
        var careportal = Store(Hour, "Weekend", ProfileActivationPhase.Requested, enteredBy: "careportal");

        (await Resolver().GetAtAsync(T0 + 2 * Hour))!.Activation.Id.Should().Be(careportal.Id);
    }

    [Fact]
    public async Task NoActivationLeavesTheProfileStoresDefault()
    {
        Store(Hour, "Rhys");

        (await Resolver().GetAtAsync(T0)).Should().BeNull();
        (await BasalRates(Resolver()).GetBasalRateAsync(T0)).Should().Be(0.5);
    }

    [Fact]
    public async Task NoActivationReadsTheTenantsDefaultStore_WhateverItIsNamed()
    {
        _therapy.Setup(r => r.GetDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TherapySettings { ProfileName = "Rhys", IsDefault = true, Timestamp = At(-24 * Hour) }]);
        Store(Hour, "Weekend");

        (await BasalRates(Resolver()).GetBasalRateAsync(T0)).Should().Be(1.0);
        var batch = await BasalRates(Resolver()).BuildResolverAsync(T0, T0 + 2 * Hour);
        batch(T0).Should().Be(1.0, "the time before the activation is the default store's");
    }

    [Fact]
    public async Task ACustomizedSnapshotIsReadAsUploadedAndNotScaledAgain()
    {
        Store(0, "Rhys", ProfileActivationPhase.Effective, percentage: 150, snapshot: Snapshot(1.5), customized: true,
            enteredBy: "openaps://AndroidAPS");

        var active = (await Resolver().GetAtAsync(T0 + Hour))!;
        active.Source.Should().Be(ProfileScheduleSource.CustomizedSnapshot);
        active.Scaling.Should().BeNull();
        (await BasalRates(Resolver()).GetBasalRateAsync(T0 + Hour)).Should().Be(1.5,
            "150% is already applied to the snapshot, so 2.25 would apply it twice");
    }

    [Fact]
    public async Task ACustomizedSnapshotMissingAKindOfScheduleGivesWayToTheStoredProfileScaled()
    {
        Store(0, "Rhys", ProfileActivationPhase.Effective, percentage: 150, snapshot: Snapshot(9.9, everyKind: false),
            customized: true, enteredBy: "openaps://AndroidAPS");

        var active = (await Resolver().GetAtAsync(T0 + Hour))!;
        active.Source.Should().Be(ProfileScheduleSource.ProfileStore,
            "a snapshot of basal alone would leave the other schedules at the hard default");
        active.Scaling!.Percentage.Should().Be(150);
        (await BasalRates(Resolver()).GetBasalRateAsync(T0 + Hour)).Should().BeApproximately(1.5, 1e-9);
    }

    [Fact]
    public async Task AStoredProfileIsScaledByTheActivationsPercentage()
    {
        Store(0, "Rhys", percentage: 110, snapshot: Snapshot(9.9));

        var active = (await Resolver().GetAtAsync(T0 + Hour))!;
        active.Source.Should().Be(ProfileScheduleSource.ProfileStore, "the store holds Rhys, so its own snapshot is not read");
        (await BasalRates(Resolver()).GetBasalRateAsync(T0 + Hour)).Should().BeApproximately(1.1, 1e-9);
    }

    [Fact]
    public async Task AMissingStoredProfileIsReadFromTheBaseSnapshotScaled()
    {
        Store(0, "Holiday", percentage: 200, snapshot: Snapshot(0.7));

        var active = (await Resolver().GetAtAsync(T0 + Hour))!;
        active.Source.Should().Be(ProfileScheduleSource.BaseSnapshot);
        active.Scaling!.Percentage.Should().Be(200);
        (await BasalRates(Resolver()).GetBasalRateAsync(T0 + Hour)).Should().BeApproximately(1.4, 1e-9);
    }

    [Fact]
    public async Task ANewerProfileStoreDocumentDoesNotEndAPermanentActivation()
    {
        Store(0, "Rhys", percentage: 120);
        _therapy.Setup(r => r.GetByProfileNameAsync("Default", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TherapySettings { ProfileName = "Default", Timestamp = At(Hour), IsDefault = true }]);

        (await Resolver().GetAtAsync(T0 + 2 * Hour))!.ProfileName.Should().Be("Rhys");
    }

    [Fact]
    public async Task TheRangeIsCutAtEachChangeOfActivationAndLeavesGapsOut()
    {
        var weekday = Store(Hour, "Weekday");
        var sick = Store(2 * Hour, "Sick", durationMs: Hour);

        var segments = await Resolver().GetRangeAsync(T0, T0 + 5 * Hour);

        segments.Select(s => (s.StartMills - T0, s.EndMills - T0, s.Active.Activation.Id)).Should().Equal(
            (Hour, 2 * Hour, weekday.Id),
            (2 * Hour, 3 * Hour, sick.Id),
            (3 * Hour, 5 * Hour, weekday.Id));
    }

    [Fact]
    public async Task ASwitchMidMinuteSplitsTheMinuteForAnEarlyThenALateLookup()
    {
        var old = Store(-Hour, "Old", end: 30_000);
        var switched = Store(30_000, "New");
        var resolver = Resolver();

        (await resolver.GetAtAsync(T0 + 10_000))!.Activation.Id.Should().Be(old.Id);
        (await resolver.GetAtAsync(T0 + 45_000))!.Activation.Id.Should().Be(switched.Id);
    }

    [Fact]
    public async Task ASwitchMidMinuteSplitsTheMinuteForALateThenAnEarlyLookup()
    {
        var old = Store(-Hour, "Old", end: 30_000);
        var switched = Store(30_000, "New");
        var resolver = Resolver();

        (await resolver.GetAtAsync(T0 + 45_000))!.Activation.Id.Should().Be(switched.Id);
        (await resolver.GetAtAsync(T0 + 10_000))!.Activation.Id.Should().Be(old.Id,
            "the minute's cached read starts at the minute, not at the first lookup");
    }

    [Fact]
    public async Task WithNoActivationBeforeASwitchMidMinuteALateLookupDoesNotLeakIntoAnEarlyOne()
    {
        var switched = Store(30_000, "New");
        var resolver = Resolver();

        (await resolver.GetAtAsync(T0 + 45_000))!.Activation.Id.Should().Be(switched.Id);
        (await resolver.GetAtAsync(T0 + 10_000)).Should().BeNull();
    }

    [Fact]
    public async Task WithNoActivationBeforeASwitchMidMinuteAnEarlyLookupDoesNotHideIt()
    {
        var switched = Store(30_000, "New");
        var resolver = Resolver();

        (await resolver.GetAtAsync(T0 + 10_000)).Should().BeNull();
        (await resolver.GetAtAsync(T0 + 45_000))!.Activation.Id.Should().Be(switched.Id);
    }

    [Fact]
    public async Task LookupsWithinOneMinuteShareOneRead()
    {
        Store(0, "Rhys");
        var resolver = Resolver();

        await resolver.GetAtAsync(T0 + 1_000);
        await resolver.GetAtAsync(T0 + 59_000);

        _repository.Verify(
            r => r.GetOverlappingAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AWriteClearsTheCachedActivations()
    {
        var cache = TestTenantCache.Create(_cache);
        var resolver = Resolver(cache);
        await resolver.GetAtAsync(T0 + Hour);

        var written = Store(0, "Rhys");
        await new ProfileActivationWriter(_repository.Object, cache).InvalidateAsync(CancellationToken.None);

        (await Resolver(cache).GetAtAsync(T0 + Hour))!.Activation.Id.Should().Be(written.Id);
    }

    [Theory]
    [MemberData(nameof(NarrowedReaders.All), MemberType = typeof(NarrowedReaders))]
    public async Task ANarrowedReaderNeverReadsTheOwnersCachedActivations(string reader)
    {
        Store(0, "Rhys");

        await Resolver().GetAtAsync(T0 + Hour);
        await Resolver(NarrowedReaders.Cache(reader, _cache)).GetAtAsync(T0 + Hour);

        _repository.Verify(
            r => r.GetOverlappingAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task AnEffectiveActivationWithoutInsulinFallsBackToTheRequestItConfirms()
    {
        var requested = Store(0, "Rhys", enteredBy: "openaps://AndroidAPS",
            snapshot: new ProfileActivationSnapshot { Insulin = new TreatmentInsulinContext { InsulinName = "Fiasp", Dia = 5, Peak = 55 } });
        var effective = Store(Hour, "Rhys", ProfileActivationPhase.Effective, enteredBy: "openaps://AndroidAPS");
        effective.RequestedActivationId = requested.Id;
        _repository.Setup(r => r.GetByIdAsync(requested.Id, It.IsAny<CancellationToken>())).ReturnsAsync(requested);

        var insulin = await new ActiveProfileResolver(Resolver(), _repository.Object, _therapy.Object).GetActiveInsulinContextAsync(T0 + 2 * Hour);

        insulin!.InsulinName.Should().Be("Fiasp");
    }
}
