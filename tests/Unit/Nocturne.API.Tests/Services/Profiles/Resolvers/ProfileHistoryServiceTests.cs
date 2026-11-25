using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

/// <summary>The versions, changes and lanes <see cref="ProfileHistoryService"/> cuts a window into.</summary>
public sealed class ProfileHistoryServiceTests : IDisposable
{
    private const long Hour = 3_600_000;

    /// <summary>2024-01-15T00:00:00Z, so times of day read in UTC are hours from here.</summary>
    private const long From = 1_705_276_800_000;
    private const long To = From + 24 * Hour;

    private readonly Mock<IActiveProfileResolver> _activeProfile = new();
    private readonly Mock<IProfileActivationResolver> _activations = new();
    private readonly Mock<IBasalScheduleRepository> _basal = new();
    private readonly Mock<ISensitivityScheduleRepository> _sensitivity = new();
    private readonly Mock<IApsSnapshotRepository> _aps = new();
    private readonly Mock<IStateSpanRepository> _stateSpans = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public ProfileHistoryServiceTests()
    {
        _activations
            .Setup(a => a.GetRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _aps.Setup(a => a.GetLoopCyclesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _stateSpans
            .Setup(s => s.GetByCategory(
                StateSpanCategory.TemporaryTarget, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    public void Dispose() => _cache.Dispose();

    private ProfileHistoryService Service() => new(
        _activeProfile.Object,
        _activations.Object,
        _basal.Object,
        Mock.Of<ICarbRatioScheduleRepository>(),
        _sensitivity.Object,
        Mock.Of<ITargetRangeScheduleRepository>(),
        Mock.Of<ITherapySettingsRepository>(),
        Mock.Of<ITherapySettingsResolver>(),
        new ProfileVersionCache(TestTenantCache.Create(_cache)),
        _aps.Object,
        _stateSpans.Object);

    private void Spans(params ProfileSpan[] spans) =>
        _activeProfile
            .Setup(a => a.GetProfileSpansForRangeAsync(From, To, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spans);

    private void BasalVersions(string profileName, params BasalSchedule[] newestFirst) =>
        _basal.Setup(r => r.GetByProfileNameAsync(profileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(newestFirst);

    private static List<ScheduleEntry> Entries(params (int Hour, double Value)[] entries) =>
        [.. entries.Select(e => new ScheduleEntry { TimeAsSeconds = e.Hour * 3600, Value = e.Value })];

    private static BasalSchedule Basal(string profileName, long fromMills, params (int Hour, double Value)[] entries) => new()
    {
        Id = Guid.NewGuid(),
        ProfileName = profileName,
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(fromMills).UtcDateTime,
        Entries = Entries(entries),
    };

    [Fact]
    public async Task OneProfileUnchanged_IsOneVersionAndNoChange()
    {
        Spans(new ProfileSpan("Default", From, To, null));
        BasalVersions("Default", Basal("Default", From - 24 * Hour, (0, 0.8), (6, 1.0)));

        var history = await Service().GetHistoryAsync(From, To);

        var only = history.Versions.Should().ContainSingle().Subject;
        only.StartMills.Should().Be(From);
        only.EndMills.Should().Be(To);
        only.Basal.Select(e => (e.TimeAsSeconds, e.Value)).Should().Equal((0, 0.8), (6 * 3600, 1.0));
        history.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEditMidWindow_CutsAVersion_AndDiffsOnlyTheScheduleThatChanged()
    {
        Spans(new ProfileSpan("Default", From, To, null));
        BasalVersions(
            "Default",
            Basal("Default", From + 10 * Hour, (0, 0.8), (6, 1.2)),
            Basal("Default", From - 24 * Hour, (0, 0.8), (6, 1.0)));

        var history = await Service().GetHistoryAsync(From, To);

        history.Versions.Select(v => (v.StartMills, v.EndMills)).Should().Equal((From, From + 10 * Hour), (From + 10 * Hour, To));
        var change = history.Changes.Should().ContainSingle().Subject;
        change.Mills.Should().Be(From + 10 * Hour);
        change.Activation.Should().BeNull();
        var basal = change.Attributes.Should().ContainSingle().Subject;
        basal.Attribute.Should().Be(ProfileAttribute.Basal);
        basal.Rows.Select(r => (r.TimeAsSeconds, r.Before, r.After, r.Changed))
            .Should().Equal((0, 0.8, 0.8, false), (6 * 3600, 1.0, 1.2, true));
    }

    [Fact]
    public async Task AReuploadOfTheSameSchedule_IsNotAChange()
    {
        Spans(new ProfileSpan("Default", From, To, null));
        BasalVersions(
            "Default",
            Basal("Default", From + 10 * Hour, (0, 0.8)),
            Basal("Default", From - 24 * Hour, (0, 0.8)));

        var history = await Service().GetHistoryAsync(From, To);

        history.Versions.Should().ContainSingle().Which.EndMills.Should().Be(To);
        history.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task APercentageSwitch_ScalesBasalForwardAndSensitivityInverse()
    {
        Spans(
            new ProfileSpan("Default", From, From + 12 * Hour, null),
            new ProfileSpan("Default", From + 12 * Hour, To, new CircadianAdjustment(80, 0)));
        BasalVersions("Default", Basal("Default", From - 24 * Hour, (0, 1.0)));
        _sensitivity.Setup(r => r.GetByProfileNameAsync("Default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SensitivitySchedule
                {
                    Id = Guid.NewGuid(),
                    ProfileName = "Default",
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(From - 24 * Hour).UtcDateTime,
                    Entries = Entries((0, 40)),
                },
            ]);

        var history = await Service().GetHistoryAsync(From, To);

        history.Versions.Should().HaveCount(2);
        history.Versions[1].Percentage.Should().Be(80);
        history.Versions[1].Basal.Single().Value.Should().Be(0.8);
        history.Versions[1].Sensitivity.Single().Value.Should().Be(50);
        var change = history.Changes.Single();
        change.PreviousPercentage.Should().BeNull();
        change.Percentage.Should().Be(80);
        change.Attributes.Select(a => a.Attribute).Should().Equal(ProfileAttribute.Basal, ProfileAttribute.Sensitivity);
    }

    [Fact]
    public async Task ATimeshift_MovesEachEntryToTheLocalTimeItTakesEffect()
    {
        Spans(new ProfileSpan("Default", From, To, new CircadianAdjustment(100, 2 * Hour)));
        BasalVersions("Default", Basal("Default", From - 24 * Hour, (0, 0.8), (6, 1.0)));

        var version = (await Service().GetHistoryAsync(From, To)).Versions.Single();

        version.TimeshiftHours.Should().Be(2);
        version.Basal.Select(e => (e.TimeAsSeconds, e.Value)).Should().Equal((0, 0.8), (4 * 3600, 1.0), (22 * 3600, 0.8));
    }

    [Fact]
    public async Task AnActivationSnapshot_IsReadInPlaceOfTheStore()
    {
        var snapshot = new ProfileActivationSnapshot { Basal = Entries((0, 0.5)) };
        Spans(new ProfileSpan("Default", From, To, null, snapshot, new CircadianAdjustment(50, 0)));
        BasalVersions("Default", Basal("Default", From - 24 * Hour, (0, 1.0)));

        var version = (await Service().GetHistoryAsync(From, To)).Versions.Single();

        version.Basal.Single().Value.Should().Be(0.5);
        version.Percentage.Should().Be(50);
    }

    [Fact]
    public async Task Lanes_FollowTheScheduleInRealTime_AndMergeAcrossMidnightlessRepeats()
    {
        Spans(new ProfileSpan("Default", From, To, null));
        BasalVersions("Default", Basal("Default", From - 24 * Hour, (0, 0.8), (6, 1.0)));

        var lanes = await Service().GetLanesAsync(From, To, loop: true, temporaryTargets: true);

        lanes.Basal.Select(s => (s.StartMills, s.EndMills, s.Value))
            .Should().Equal((From, From + 6 * Hour, 0.8), (From + 6 * Hour, To, 1.0));
        lanes.Sensitivity.Should().BeEmpty();
    }

    [Fact]
    public async Task Lanes_DivideTheScheduledSensitivityByTheLoopRatio()
    {
        Spans(new ProfileSpan("Default", From, To, null));
        _sensitivity.Setup(r => r.GetByProfileNameAsync("Default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SensitivitySchedule
                {
                    Id = Guid.NewGuid(),
                    ProfileName = "Default",
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(From - 24 * Hour).UtcDateTime,
                    Entries = Entries((0, 40), (12, 60)),
                },
            ]);
        _aps.Setup(a => a.GetLoopCyclesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Cycle(From - Hour, 1.5),
                Cycle(From + Hour, 0.8),
                Cycle(From + 2 * Hour, null),
                Cycle(From + 13 * Hour, 1.2),
            ]);

        var lanes = await Service().GetLanesAsync(From, To, loop: true, temporaryTargets: true);

        lanes.Loop!.Select(p => (p.Mills, p.SensitivityRatio, p.Sensitivity))
            .Should().Equal((From + Hour, 0.8, 50), (From + 13 * Hour, 1.2, 50));
    }

    [Fact]
    public async Task Lanes_ReadTemporaryTargetsInMgdl_ClippedToTheWindow_WithoutCancelledOnes()
    {
        Spans(new ProfileSpan("Default", From, To, null));
        _stateSpans
            .Setup(s => s.GetByCategory(
                StateSpanCategory.TemporaryTarget, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Target(From + 2 * Hour, From + 3 * Hour, nameof(TemporaryTargetState.Cancelled), 100, 100, null),
                Target(From - Hour, From + Hour, nameof(TemporaryTargetState.Active), 8, 7, "mmol"),
            ]);

        var lanes = await Service().GetLanesAsync(From, To, loop: true, temporaryTargets: true);

        var only = lanes.TemporaryTargets.Should().ContainSingle().Subject;
        only.StartMills.Should().Be(From);
        only.EndMills.Should().Be(From + Hour);
        only.Value.Should().BeApproximately(126, 0.5);
        only.High.Should().BeApproximately(144, 0.5);
    }

    [Fact]
    public async Task Lanes_LeaveOutWhatTheCallerMayNotRead()
    {
        Spans(new ProfileSpan("Default", From, To, null));

        var lanes = await Service().GetLanesAsync(From, To, loop: false, temporaryTargets: false);

        lanes.Loop.Should().BeNull();
        lanes.TemporaryTargets.Should().BeNull();
        _aps.Verify(
            a => a.GetLoopCyclesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _stateSpans.Verify(
            s => s.GetByCategory(
                It.IsAny<StateSpanCategory>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static ApsLoopCycle Cycle(long mills, double? ratio) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime,
        SensitivityRatio = ratio,
    };

    private static StateSpan Target(long start, long end, string state, double top, double bottom, string? units)
    {
        var metadata = new Dictionary<string, object> { ["targetTop"] = top, ["targetBottom"] = bottom };
        if (units is not null)
            metadata["units"] = units;
        return new StateSpan
        {
            Category = StateSpanCategory.TemporaryTarget,
            State = state,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(end).UtcDateTime,
            Metadata = metadata,
        };
    }
}
