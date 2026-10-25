using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

/// <summary>The profiles and schedule versions <see cref="ActiveProfileSchedulesService"/> reports for a window.</summary>
public sealed class ActiveProfileSchedulesServiceTests : IDisposable
{
    private const long Hour = 3_600_000;
    private const long From = 1_705_320_000_000;
    private const long To = From + 4 * Hour;

    private readonly Mock<IActiveProfileResolver> _activeProfile = new();
    private readonly Mock<IBasalScheduleRepository> _basal = new();
    private readonly Mock<ICarbRatioScheduleRepository> _carbRatio = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public void Dispose() => _cache.Dispose();

    private ActiveProfileSchedulesService Service() => new(
        _activeProfile.Object,
        _basal.Object,
        _carbRatio.Object,
        Mock.Of<ISensitivityScheduleRepository>(),
        Mock.Of<ITargetRangeScheduleRepository>(),
        new ProfileVersionCache(TestTenantCache.Create(_cache)));

    private void Spans(params ProfileSpan[] spans) =>
        _activeProfile
            .Setup(a => a.GetProfileSpansForRangeAsync(From, To, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spans);

    private void BasalVersions(string profileName, params BasalSchedule[] newestFirst) =>
        _basal.Setup(r => r.GetByProfileNameAsync(profileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(newestFirst);

    private static BasalSchedule Version(string profileName, long fromMills, double rate) => new()
    {
        Id = Guid.NewGuid(),
        ProfileName = profileName,
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(fromMills).UtcDateTime,
        Entries = [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = rate }],
    };

    [Fact]
    public async Task OneProfileAllWindow_IsOneEntryWithTheWholeShare()
    {
        Spans(new ProfileSpan("Default", From, To, null));
        BasalVersions("Default", Version("Default", From - 24 * Hour, 0.8));

        var profiles = await Service().GetForRangeAsync(From, To);

        var only = profiles.Should().ContainSingle().Subject;
        only.ProfileName.Should().Be("Default");
        only.ActiveMs.Should().Be(To - From);
        only.Share.Should().Be(1);
        only.Basal.Should().ContainSingle().Which.Value.Should().Be(0.8);
        only.BasalChanges.ChangedDuringPeriod.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchMidWindow_IsOneEntryPerProfile_LongestFirst_SharesSummingToOne()
    {
        Spans(
            new ProfileSpan("Exercise", From, From + Hour, new CircadianAdjustment(80, 2 * Hour)),
            new ProfileSpan("Default", From + Hour, To, null));
        BasalVersions("Default", Version("Default", From - 24 * Hour, 0.8));
        BasalVersions("Exercise", Version("Exercise", From - 24 * Hour, 0.5));

        var profiles = await Service().GetForRangeAsync(From, To);

        profiles.Select(p => p.ProfileName).Should().Equal("Default", "Exercise");
        profiles[0].Share.Should().BeApproximately(0.75, 1e-9);
        profiles[1].Share.Should().BeApproximately(0.25, 1e-9);
        profiles.Sum(p => p.Share).Should().BeApproximately(1, 1e-9);
        profiles[0].Basal.Single().Value.Should().Be(0.8);
        profiles[1].Basal.Single().Value.Should().Be(0.5);
        profiles[0].Percentage.Should().BeNull();
        profiles[1].Percentage.Should().Be(80);
        profiles[1].TimeshiftHours.Should().Be(2);
        profiles[1].AdjustmentApplied.Should().BeFalse();
    }

    [Fact]
    public async Task AnEditAfterTheWindow_LeavesTheVersionInForce()
    {
        Spans(new ProfileSpan("Default", From, To, null));
        BasalVersions("Default", Version("Default", To + Hour, 1.5), Version("Default", From - 24 * Hour, 0.8));

        var only = (await Service().GetForRangeAsync(From, To)).Single();

        only.Basal.Single().Value.Should().Be(0.8);
        only.BasalChanges.ChangedDuringPeriod.Should().BeFalse();
    }

    [Fact]
    public async Task AnEditMidWindow_ReadsTheVersionInForceLongest_AndCountsTheChange()
    {
        Spans(new ProfileSpan("Default", From, To, null));
        BasalVersions("Default", Version("Default", From + Hour, 1.5), Version("Default", From - 24 * Hour, 0.8));

        var only = (await Service().GetForRangeAsync(From, To)).Single();

        only.Basal.Single().Value.Should().Be(1.5);
        only.BasalChanges.ChangedDuringPeriod.Should().BeTrue();
        only.BasalChanges.ChangeCount.Should().Be(1);
        only.BasalChanges.LastChangedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(From + Hour).UtcDateTime);
    }

    [Fact]
    public async Task AnActivationCarryingSchedules_IsReadInPlaceOfTheStore()
    {
        var snapshot = new ProfileActivationSnapshot
        {
            Basal = [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = 0.6 }],
        };
        Spans(new ProfileSpan("Default", From, To, null, snapshot));
        BasalVersions("Default", Version("Default", From - 24 * Hour, 0.8));

        var only = (await Service().GetForRangeAsync(From, To)).Single();

        only.Basal.Single().Value.Should().Be(0.6);
    }

    [Fact]
    public async Task AProfileReadInSeveralStretches_IsOneEntry_SharesSummingToOne()
    {
        Spans(
            new ProfileSpan("Default", From, From + Hour, null),
            new ProfileSpan("Exercise", From + Hour, From + 2 * Hour, null),
            new ProfileSpan("Default", From + 2 * Hour, To, null));
        BasalVersions("Default", Version("Default", From - 24 * Hour, 0.8));
        BasalVersions("Exercise", Version("Exercise", From - 24 * Hour, 0.5));

        var profiles = await Service().GetForRangeAsync(From, To);

        profiles.Select(p => p.ProfileName).Should().Equal("Default", "Exercise");
        profiles[0].ActiveMs.Should().Be(3 * Hour);
        profiles[0].Share.Should().BeApproximately(0.75, 1e-9);
        profiles.Sum(p => p.Share).Should().BeApproximately(1, 1e-9);
        profiles[0].BasalChanges.ChangedDuringPeriod.Should().BeFalse();
    }

    [Fact]
    public async Task AWindowOpeningBeforeTheFirstVersion_ReadsTheOldest_NeverAnEditMadeAfterIt()
    {
        Spans(new ProfileSpan("Default", From, To, null));
        BasalVersions("Default", Version("Default", To + Hour, 1.5), Version("Default", From + Hour, 0.8));

        var only = (await Service().GetForRangeAsync(From, To)).Single();

        only.Basal.Single().Value.Should().Be(0.8);
        only.BasalChanges.ChangedDuringPeriod.Should().BeFalse();
    }

    [Fact]
    public async Task AVersionWrittenWithTheSameTimesAndValues_IsNotAChange()
    {
        Spans(new ProfileSpan("Default", From, To, null));
        BasalVersions(
            "Default",
            new BasalSchedule
            {
                ProfileName = "Default",
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(From + Hour).UtcDateTime,
                Entries = [new ScheduleEntry { Time = "00:00:00", TimeAsSeconds = null, Value = 0.8 }],
            },
            Version("Default", From - 24 * Hour, 0.8));

        var only = (await Service().GetForRangeAsync(From, To)).Single();

        only.BasalChanges.ChangedDuringPeriod.Should().BeFalse();
    }

    [Fact]
    public async Task AHundredPercentWithNoShift_CountsAsNoAdjustment()
    {
        Spans(
            new ProfileSpan("Default", From, From + 3 * Hour / 2, new CircadianAdjustment(120, 0)),
            new ProfileSpan("Default", From + 3 * Hour / 2, From + 11 * Hour / 4, null),
            new ProfileSpan("Default", From + 11 * Hour / 4, To, new CircadianAdjustment(100, 0)));

        var only = (await Service().GetForRangeAsync(From, To)).Single();

        only.Percentage.Should().BeNull();
        only.TimeshiftHours.Should().BeNull();
    }

    [Fact]
    public async Task ASnapshotUploadedWithItsPercentageApplied_IsLabelledApplied()
    {
        var snapshot = new ProfileActivationSnapshot
        {
            Basal = [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = 0.64 }],
        };
        Spans(new ProfileSpan("Default", From, To, null, snapshot, new CircadianAdjustment(80, 0)));

        var only = (await Service().GetForRangeAsync(From, To)).Single();

        only.Percentage.Should().Be(80);
        only.AdjustmentApplied.Should().BeTrue();
        only.Basal.Single().Value.Should().Be(0.64, "the snapshot already carries the percentage");
    }

    [Fact]
    public async Task ASnapshotMissingAKindOfSchedule_LeavesItEmpty_RatherThanReadingTheStore()
    {
        var snapshot = new ProfileActivationSnapshot
        {
            Basal = [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = 0.6 }],
        };
        Spans(new ProfileSpan("Default", From, To, null, snapshot));
        _carbRatio.Setup(r => r.GetByProfileNameAsync("Default", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CarbRatioSchedule
            {
                ProfileName = "Default",
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(From - 24 * Hour).UtcDateTime,
                Entries = [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = 10 }],
            }]);

        var only = (await Service().GetForRangeAsync(From, To)).Single();

        only.Basal.Single().Value.Should().Be(0.6);
        only.CarbRatio.Should().BeEmpty();
    }
}
