using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

public partial class AnalyticsReadScopeGuardTests
{
    private static LoopAnalysisAggregate EventAggregate() => new() { P50 = 1, Mean = 1, N = 1 };

    private static EventResponseRange EventRange(int cohort = 1) => new()
    {
        Cohorts =
        [
            new EventCohortResult
            {
                Cohort = cohort,
                Anchors = 1,
                Events = 1,
                GramsPerUnitNotIsolated = 1,
                Slots =
                [
                    new EventResponseSlot
                    {
                        Glucose = EventAggregate(), GlucoseChange = EventAggregate(), NetLoopCorrection = EventAggregate(),
                        NetInsulinSinceAnchor = EventAggregate(), NetLoopCorrectionSinceAnchor = EventAggregate(),
                        Iob = EventAggregate(), Cob = EventAggregate(),
                    },
                ],
                Metrics = new EventMetricsSummary { StartGlucose = EventAggregate(), NetInsulin = EventAggregate(), IobAtAnchor = EventAggregate() },
            },
        ],
        Events = [new EventResponseEvent { Date = "2024-06-03", Metrics = new EventMetrics { StartGlucose = 100, NetInsulin = 2, IobAtAnchor = 1 } }],
    };

    private static EventResponseReport EventReport() => new() { Primary = EventRange(), Comparison = EventRange() };

    public static TheoryData<string[], LoopAnalysisSeries[]> EventScopeCombinations => new()
    {
        { AllLoopAnalysisCategories, [] },
        { [Scope.GlucoseRead, Scope.DevicesRead], [LoopAnalysisSeries.EventNetInsulin, LoopAnalysisSeries.EventIob, LoopAnalysisSeries.EventCob, LoopAnalysisSeries.EventList] },
    };

    [Theory]
    [MemberData(nameof(EventScopeCombinations))]
    public void LoopAnalysis_Events_WithheldSeries_ListExactlyWhatIsEmptied(string[] scopes, LoopAnalysisSeries[] expected)
    {
        var data = LoopAnalysisReadScopeGuard.Redact(EventReport(), Granted(scopes));

        data.WithheldSeries.Should().BeEquivalentTo([.. expected, .. LoopAnalysisReadScopeGuard.WithheldEnvelope(Granted(scopes))]);
        bool Withheld(LoopAnalysisSeries series) => expected.Contains(series);
        foreach (var range in new[] { data.Primary, data.Comparison! })
        {
            var cohort = range.Cohorts.Single();
            var slot = cohort.Slots.Single();
            slot.Glucose.Should().NotBeNull();
            cohort.Metrics.StartGlucose.Should().NotBeNull();
            (slot.NetLoopCorrection is null).Should().Be(Withheld(LoopAnalysisSeries.EventNetInsulin));
            (slot.NetInsulinSinceAnchor is null).Should().Be(Withheld(LoopAnalysisSeries.EventNetInsulin));
            (cohort.Metrics.NetInsulin is null).Should().Be(Withheld(LoopAnalysisSeries.EventNetInsulin));
            (cohort.GramsPerUnitNotIsolated == 0).Should().Be(Withheld(LoopAnalysisSeries.EventNetInsulin));
            (slot.Iob is null).Should().Be(Withheld(LoopAnalysisSeries.EventIob));
            (cohort.Metrics.IobAtAnchor is null).Should().Be(Withheld(LoopAnalysisSeries.EventIob));
            (slot.Cob is null).Should().Be(Withheld(LoopAnalysisSeries.EventCob));
            (range.Events.Count == 0).Should().Be(Withheld(LoopAnalysisSeries.EventList));
        }
    }

    public static TheoryData<string[]> ScopesWithoutGlucose => new()
    {
        Array.Empty<string>(),
        new[] { Scope.TreatmentsRead, Scope.DevicesRead, Scope.TherapyRead },
    };

    [Theory]
    [MemberData(nameof(ScopesWithoutGlucose))]
    public void LoopAnalysis_Events_WithoutGlucose_LeaveNoCohortCountOrEvent(string[] scopes)
    {
        var data = LoopAnalysisReadScopeGuard.Redact(RedactionCoverage.Populated<EventResponseReport>(), Granted(scopes));

        RedactionCoverage.AssertWithheld(
            data, [typeof(EventResponseRange)], ["EventResponseRange.From", "EventResponseRange.To"]);
    }

    [Fact]
    public void LoopAnalysis_Events_WithGlucoseAndDevices_KeepOnlyTheGlucoseSeriesAndTheCounts()
    {
        RedactionCoverage.AssertWithheld(
            LoopAnalysisReadScopeGuard.Redact(RedactionCoverage.Populated<EventResponseReport>(), Granted(Scope.GlucoseRead, Scope.DevicesRead)),
            [typeof(EventCohortResult), typeof(EventResponseSlot), typeof(EventMetricsSummary)],
            [
                "EventCohortResult.Cohort", "EventCohortResult.Response", "EventCohortResult.Anchors", "EventCohortResult.NotIsolated",
                "EventCohortResult.MissingAnchorGlucose", "EventCohortResult.BeforeVisibleHistory", "EventCohortResult.Events",
                "EventCohortResult.WentBelowRange", "EventCohortResult.WentBelowRangeObserved", "EventCohortResult.Slots", "EventCohortResult.Metrics",
                "EventResponseSlot.GlucoseChange", "EventResponseSlot.Glucose",
                "EventMetricsSummary.StartGlucose", "EventMetricsSummary.PeakRise", "EventMetricsSummary.MinutesToPeak",
                "EventMetricsSummary.GlucoseAt2Hours", "EventMetricsSummary.GlucoseAt4Hours", "EventMetricsSummary.MinutesBackToStart",
                "EventMetricsSummary.Drop", "EventMetricsSummary.Nadir", "EventMetricsSummary.MinutesToNadir",
            ]);
    }

    [Fact]
    public void LoopAnalysis_Events_WithGlucoseAndTreatments_KeepEveryFigureOfEachEvent()
    {
        var data = LoopAnalysisReadScopeGuard.Redact(
            RedactionCoverage.Populated<EventResponseReport>(), Granted(Scope.GlucoseRead, Scope.TreatmentsRead));

        data.WithheldSeries.Should().Equal([LoopAnalysisSeries.ProfileNames], "only the envelope's profile names need therapy");
        data.Primary.Events.Single().Metrics.GramsPerUnit.Should().NotBeNull();
        data.Primary.Events.Single().Metrics.DropPerUnit.Should().NotBeNull();
    }

    public static TheoryData<EventAnchorKind, string[], bool> AnchorAccess => new()
    {
        { EventAnchorKind.Meal, [Scope.GlucoseRead, Scope.DevicesRead], false },
        { EventAnchorKind.Meal, [Scope.TreatmentsRead], false },
        { EventAnchorKind.Meal, [Scope.GlucoseRead, Scope.TreatmentsRead], true },
        { EventAnchorKind.CorrectionBolus, [Scope.GlucoseRead], false },
        { EventAnchorKind.ExerciseStart, [Scope.GlucoseRead, Scope.TreatmentsRead], true },
        { EventAnchorKind.SensorStart, [Scope.GlucoseRead, Scope.TreatmentsRead], false },
        { EventAnchorKind.SensorStart, [Scope.DevicesRead], false },
        { EventAnchorKind.SensorStart, [Scope.GlucoseRead, Scope.DevicesRead], true },
        { EventAnchorKind.UnannouncedRise, [Scope.GlucoseRead], false },
        { EventAnchorKind.UnannouncedRise, [Scope.TreatmentsRead], false },
        { EventAnchorKind.UnannouncedRise, [Scope.GlucoseRead, Scope.TreatmentsRead], true },
    };

    [Theory]
    [MemberData(nameof(AnchorAccess))]
    public void LoopAnalysis_Events_ACohortNeedsGlucoseAndTheCategoryItsAnchorIsDrawnFrom(EventAnchorKind anchor, string[] scopes, bool kept)
    {
        var definition = new EventResponseDefinition
        {
            Cohorts = [new EventCohortDefinition { Cohort = 2, Anchor = anchor }],
        };

        var ignored = LoopAnalysisReadScopeGuard.NarrowEvents(definition, Granted(scopes));

        definition.Cohorts.Should().HaveCount(kept ? 1 : 0);
        ignored.Should().Equal(kept ? [] : ["anchor2"]);
        LoopAnalysisReadScopeGuard.UnavailableAnchors(Granted(scopes)).Select(a => a.Anchor)
            .Contains(anchor).Should().Be(!kept);
    }

    [Fact]
    public void LoopAnalysis_Events_WithoutTreatments_DropTheOptions()
    {
        var definition = new EventResponseDefinition { IsolatedOnly = true, RequireCorroboration = true };

        var ignored = LoopAnalysisReadScopeGuard.NarrowEvents(definition, Granted(Scope.GlucoseRead, Scope.DevicesRead));

        ignored.Should().Equal("isolatedOnly", "requireCorroboration");
        definition.IsolatedOnly.Should().BeFalse();
        definition.RequireCorroboration.Should().BeFalse();
        LoopAnalysisReadScopeGuard.UnavailableEventFilters(Granted(Scope.GlucoseRead)).Select(f => f.Filter)
            .Should().Equal("isolatedOnly", "requireCorroboration");
        LoopAnalysisReadScopeGuard.UnavailableEventFilters(Granted(Scope.TreatmentsRead)).Should().BeEmpty();
    }

    [Fact]
    public void LoopAnalysis_Events_OptionNames_AreTheQueryParametersTheBinderReads()
    {
        var parameters = typeof(EventResponseQuery).GetProperties()
            .Select(p => p.GetCustomAttribute<FromQueryAttribute>()?.Name)
            .OfType<string>()
            .ToHashSet();

        LoopAnalysisReadScopeGuard.UnavailableEventFilters(Granted()).Select(f => f.Filter)
            .Should().OnlyContain(name => parameters.Contains(name));
        parameters.Should().Contain(["anchor1", "anchor2", "anchor3"]);
    }

    [Fact]
    public async Task LoopAnalysis_EventHandler_NarrowsTheDefinitionBeforeComputingAndReportsWhatItIgnored()
    {
        var (controller, service) = LoopAnalysisHandler(
            Granted(Scope.ReportsRead, Scope.GlucoseRead, Scope.DevicesRead), query: "?anchor1=Meal&anchor2=SensorStart&isolatedOnly=true");
        service
            .Setup(s => s.GetEventResponseAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<EventResponseDefinition>(), It.IsAny<TimeZoneInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new EventResponseReport { Primary = EventRange(2) });

        var result = await controller.GetEventResponse(
            Week(), new EventResponseQuery { Anchor1 = EventAnchorKind.Meal, Anchor2 = EventAnchorKind.SensorStart, IsolatedOnly = true });

        var data = result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<EventResponseReport>().Subject;
        data.IgnoredFilters.Should().BeEquivalentTo(["anchor1", "isolatedOnly"]);
        data.Definition.Cohorts.Select(c => c.Anchor).Should().Equal(EventAnchorKind.SensorStart);
        data.SlotMinutes.Should().HaveCount(72).And.StartWith(-60);
        data.BackToStartMgdl.Should().Be(20);
        data.UnavailableAnchors.Select(a => a.Anchor).Should().Contain(EventAnchorKind.Meal).And.NotContain(EventAnchorKind.SensorStart);
        data.UnavailableFilters.Select(f => f.Filter).Should().Contain(["isolatedOnly", "requireCorroboration", "excludeOverrides"]);
        data.Primary.Events.Should().BeEmpty();
        data.Primary.Cohorts.Single().Slots.Single().Glucose.Should().NotBeNull();
        service.Verify(s => s.GetEventResponseAsync(
            It.IsAny<LoopAnalysisFilter>(),
            It.Is<EventResponseDefinition>(d => d.Cohorts.Count == 1 && d.Cohorts[0].Anchor == EventAnchorKind.SensorStart && !d.IsolatedOnly),
            It.IsAny<TimeZoneInfo>(),
            It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData("minutesAfter", 482)]
    [InlineData("minutesBefore", 7)]
    public async Task LoopAnalysis_EventHandler_AppliesTheDefaultForAWindowOutsideTheChoices(string parameter, int minutes)
    {
        var (controller, service) = LoopAnalysisHandler(Granted([Scope.ReportsRead, .. AllLoopAnalysisCategories]));
        service
            .Setup(s => s.GetEventResponseAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<EventResponseDefinition>(), It.IsAny<TimeZoneInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new EventResponseReport { Primary = EventRange(2) });
        var events = new EventResponseQuery();
        if (parameter == "minutesAfter") events.MinutesAfter = minutes; else events.MinutesBefore = minutes;

        var result = await controller.GetEventResponse(Week(), events);

        var data = result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<EventResponseReport>().Subject;
        data.IgnoredFilters.Should().Contain(parameter);
        data.Definition.MinutesBefore.Should().Be(EventResponseDefinition.DefaultMinutesBefore);
        data.Definition.MinutesAfter.Should().Be(EventResponseDefinition.DefaultMinutesAfter);
        data.Limits.MinutesAfterChoices.Should().Equal(EventResponseDefinition.MinutesAfterChoices);
    }

    [Fact]
    public void LoopAnalysis_EventQuery_NamesTheValuesOutsideTheLimits_AndLeavesThemOut()
    {
        var query = new EventResponseQuery
        {
            Anchor1 = EventAnchorKind.Meal,
            MinGrams1 = -1,
            MaxGrams1 = EventResponseDefinition.MaxGrams + 1,
            MinutesBefore = EventResponseDefinition.MinutesBeforeChoices[^1],
            MinutesAfter = 485,
        };

        query.OutOfLimits().Should().BeEquivalentTo(["minutesAfter", "minGrams1", "maxGrams1"]);
        var definition = query.ToDefinition();
        (definition.Cohorts.Single().MinGrams, definition.Cohorts.Single().MaxGrams).Should().Be((null, null));
        definition.MinutesBefore.Should().Be(EventResponseDefinition.MinutesBeforeChoices[^1]);
        definition.MinutesAfter.Should().Be(EventResponseDefinition.DefaultMinutesAfter);
    }

    [Fact]
    public void LoopAnalysis_EventQuery_DefaultsToEveryMeal_AndReadsAnInvertedFilterLeniently()
    {
        new EventResponseQuery().ToDefinition().Cohorts.Should().ContainSingle()
            .Which.Anchor.Should().Be(EventAnchorKind.Meal);
        new EventResponseQuery { Anchor2 = EventAnchorKind.CorrectionBolus }.ToDefinition().Cohorts
            .Should().ContainSingle().Which.Cohort.Should().Be(2);
        new EventResponseQuery { Anchor1 = EventAnchorKind.CorrectionBolus, MinGrams1 = 10 }.Problem().Should().NotBeNull();
        new EventResponseQuery { FromMinute2 = 60 }.Problem().Should().NotBeNull();

        var lenient = new EventResponseQuery { FromMinute1 = 60, ToMinute1 = 60, MinGrams1 = 80, MaxGrams1 = 20 };
        lenient.Problem().Should().BeNull();
        var cohort = lenient.ToDefinition().Cohorts.Single();
        (cohort.FromMinute, cohort.ToMinute).Should().Be((null, null), "the same start and end is the whole day");
        (cohort.MinGrams, cohort.MaxGrams).Should().Be((20, 80));
    }

    [Theory]
    [InlineData(nameof(EventResponseQuery.FromMinute1), 1440)]
    [InlineData(nameof(EventResponseQuery.ToMinute3), -1)]
    public void LoopAnalysis_EventQuery_BoundsEveryMinuteOfTheDay(string property, int value)
    {
        var info = typeof(EventResponseQuery).GetProperty(property)!;
        var range = info.GetCustomAttribute<System.ComponentModel.DataAnnotations.RangeAttribute>();

        range.Should().NotBeNull();
        range!.IsValid(value).Should().BeFalse();
    }
}
