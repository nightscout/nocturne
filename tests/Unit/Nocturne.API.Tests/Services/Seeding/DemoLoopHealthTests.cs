using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Seeding;
using Nocturne.API.Services.V4;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Services.Demo.Services;
using Xunit;
using Aid = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>
/// The sample tenant's history read back as Loop health reads a real Trio upload: device statuses
/// are decomposed into snapshots and projected into loop cycles as the repository projects them,
/// and each temp basal is judged against its own device's snapshots as
/// <see cref="TempBasalOriginCorroborator"/> judges it.
/// </summary>
public class DemoLoopHealthTests
{
    internal const int Days = 7;

    internal static readonly DateTime End = new(2026, 10, 20, 20, 0, 0);

    internal sealed record Run(
        List<DemoTimeStep> Steps,
        List<Entry> Readings,
        List<Aid.ApsSnapshot> Snapshots,
        List<Aid.ApsLoopCycle> Cycles,
        List<(Treatment Source, Aid.TempBasal Temp, Aid.TempBasalOrigin Default)> Temps,
        LoopHealthRange Health);

    internal static Run Simulate() => Simulate(End);

    internal static Run Simulate(DateTime end)
    {
        var config = SampleDataSeeder.ConfigurationFor(Days);
        var steps = new DemoDataGenerator(
                Options.Create(config), NullLogger<DemoDataGenerator>.Instance, NullLoggerFactory.Instance)
            .GenerateHistoricalTimeline(end)
            .ToList();

        var readings = new List<Entry>();
        var snapshots = new List<Aid.ApsSnapshot>();
        foreach (var step in steps)
        {
            readings.AddRange(DemoCoverageSeeds.CgmReadings(step));
            if (DemoDeviceStatusGenerator.ForStep(step, config.TargetGlucose, config.BasalRate) is not { } status)
                continue;
            var snapshot = DeviceStatusDecomposer.MapToApsSnapshot(status, status.Mills.ToString("x24"), SampleDataSeeder.DevSampleDataSource, null)!;
            snapshot.Id = Guid.NewGuid();
            snapshots.Add(snapshot);
        }

        var cycles = snapshots.Select(Cycle).ToList();
        var proposals = snapshots.ToLookup(s => s.Device, Proposal);

        var temps = new List<(Treatment, Aid.TempBasal, Aid.TempBasalOrigin)>();
        foreach (var treatment in steps.SelectMany(s => s.Treatments).Where(t => t.EventType == "Temp Basal"))
        {
            var temp = TreatmentDecomposer.MapToTempBasal(treatment, null);
            var uploaderDefault = temp.Origin;
            var verdict = TempBasalOriginCorroborator.Judge(
                uploaderDefault, temp.StartTimestamp, temp.Rate, temp.Device is null ? [] : proposals[temp.Device]);
            temp.Origin = verdict.Origin;
            temp.OriginEvidence = verdict.Evidence;
            temp.ApsSnapshotId = verdict.SnapshotId;
            temps.Add((treatment, temp, uploaderDefault));
        }

        var ordered = temps.OrderBy(t => t.Item2.StartMills).ToList();
        var delivered = ordered.Select((t, i) =>
        {
            var start = t.Item2.StartMills;
            var end = Math.Min(
                t.Item2.EndTimestamp is { } e ? new DateTimeOffset(e, TimeSpan.Zero).ToUnixTimeMilliseconds() : start,
                i + 1 < ordered.Count ? ordered[i + 1].Item2.StartMills : long.MaxValue);
            return new DeliveredBasalSegment(
                start, end, t.Item2.Rate ?? 0,
                t.Item2.Origin == Aid.TempBasalOrigin.Algorithm ? BasalDeliveryOrigin.Algorithm : BasalDeliveryOrigin.Manual);
        }).ToList();

        var from = new DateTimeOffset(steps[0].Time).ToUnixTimeMilliseconds();
        var to = new DateTimeOffset(steps[^1].Time).ToUnixTimeMilliseconds() + LoopHealthCalculator.CycleCreditMs;
        var zone = TimeZoneInfo.Local;
        var inputs = new LoopHealthInputs
        {
            Zone = zone,
            Cycles = cycles,
            Insulin = new NetInsulinAboveSchedule(delivered, [], []),
            Evidence = new BasalEvidence(IntervalSet.Empty, IntervalSet.Empty, IntervalSet.Empty),
            Temps = [.. ordered.Select(t => t.Item2)],
        };
        var manualModes = DemoLifestyleSeeds.BuildSpans(end.Date, Days, end)
            .Where(s => s.Category == StateSpanCategory.PumpMode && s.State == nameof(PumpModeState.Manual))
            .Select(s => new MillsInterval(Mills(s.StartLocal), s.EndLocal is { } end ? Mills(end) : to));
        var sources = new LoopAnalysisMaskSources
        {
            OpenLoop = LoopAnalysisMask.OpenLoop(manualModes, [], [], cycles.Select(c => c.Mills)).Intervals,
        };
        var mask = LoopAnalysisMask.Build(
            new LoopAnalysisMasks { ExcludeIllness = false, ExcludeOpenLoop = true }, from, to, zone, sources);

        return new Run(steps, readings, snapshots, cycles, ordered, LoopHealthCalculator.Calculate(inputs, mask, from, to));
    }

    private static long Mills(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();

    /// <summary>
    /// The row <see cref="ApsSnapshotRepository.GetLoopCyclesAsync"/> reads, with the two keys it
    /// reads out of the stored suggestion in SQL read here from the same JSON.
    /// </summary>
    private static Aid.ApsLoopCycle Cycle(Aid.ApsSnapshot snapshot)
    {
        using var suggested = snapshot.SuggestedJson is { } json ? JsonDocument.Parse(json) : null;
        return ApsSnapshotRepository.ToLoopCycle(new ApsSnapshotRepository.LoopCycleColumns
        {
            Id = snapshot.Id,
            Timestamp = snapshot.Timestamp,
            AidAlgorithm = snapshot.AidAlgorithm.ToString(),
            Enacted = snapshot.Enacted,
            EnactedRate = snapshot.EnactedRate,
            Iob = snapshot.Iob,
            SensitivityRatio = snapshot.SensitivityRatio,
            Reason = suggested?.RootElement.TryGetProperty("reason", out var reason) == true ? reason.GetString() : null,
            SuggestedChange = suggested is null
                ? null
                : suggested.RootElement.TryGetProperty("duration", out _) || suggested.RootElement.TryGetProperty("units", out _),
        });
    }

    /// <summary>What <c>ProposalSql</c> reads of a snapshot: its enactment's own time, and its rates.</summary>
    private static TempBasalOriginCorroborator.Proposal Proposal(Aid.ApsSnapshot snapshot)
    {
        using var enacted = snapshot.EnactedJson is { } e ? JsonDocument.Parse(e) : null;
        using var suggested = snapshot.SuggestedJson is { } s ? JsonDocument.Parse(s) : null;
        DateTime? enactedAt = enacted?.RootElement.TryGetProperty("timestamp", out var at) == true
            ? DateTimeOffset.Parse(at.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime
            : null;
        double? suggestedRate = suggested?.RootElement.TryGetProperty("rate", out var rate) == true ? rate.GetDouble() : null;
        return new(snapshot.Id, snapshot.Timestamp, enactedAt, snapshot.Enacted ? snapshot.EnactedRate : null, suggestedRate);
    }

    private static bool CanCycle(DemoTimeStep step) => step.Loop is DemoLoopState.Closed or DemoLoopState.Manual;

    [Fact]
    public void SampleLoop_RecordsACycleEveryFiveMinutesWhileItCanRun()
    {
        var run = Simulate();

        run.Steps.Select(s => s.Loop).Should().Contain(new[]
            { DemoLoopState.Manual, DemoLoopState.SiteChange, DemoLoopState.SensorWarmUp, DemoLoopState.PhoneOutOfRange });
        run.Cycles.Select(c => c.Mills).Should().Equal(run.Steps.Where(CanCycle).Select(s => Mills(s.Time)));
    }

    [Fact]
    public void SampleLoop_IsClosedForARealisticShareOfTheRange()
    {
        var run = Simulate();

        var closedLoop = run.Health.Activity.ClosedLoop;
        closedLoop.Should().NotBeNull();
        closedLoop!.Percent.Should().BeInRange(85, 97);
    }

    [Fact]
    public void SampleLoop_InManualModeSuggestsAndEnactsNothing()
    {
        var run = Simulate();

        var manual = run.Steps.Where(s => s.Loop == DemoLoopState.Manual).Select(s => Mills(s.Time)).ToHashSet();
        var cycles = run.Cycles.Where(c => manual.Contains(c.Mills)).ToList();
        cycles.Should().HaveCount(manual.Count);
        cycles.Should().OnlyContain(c => !c.Enacted && c.SuggestedChange == true);
    }

    [Fact]
    public void SampleLoop_NoTempRunsIntoAnInterruption()
    {
        var run = Simulate();

        run.Temps.Should().NotBeEmpty();
        foreach (var (source, temp, _) in run.Temps)
        {
            var end = temp.EndTimestamp!.Value;
            run.Steps.Where(s => s.Time.ToUniversalTime() >= temp.StartTimestamp && s.Time.ToUniversalTime() < end)
                .Should().OnlyContain(s => s.Loop == DemoLoopState.Closed, $"the temp at {source.Created_at} ends by then");
        }
    }

    [Fact]
    public void SampleLoop_HasNoGlucoseWhileANewSensorWarmsUp()
    {
        var run = Simulate();

        var warmUp = run.Steps.Where(s => s.Loop == DemoLoopState.SensorWarmUp).ToList();
        warmUp.Should().NotBeEmpty();
        var first = Mills(warmUp[0].Time);
        var last = Mills(warmUp[^1].Time) + LoopHealthCalculator.CycleCreditMs;
        run.Readings.Should().NotContain(r => r.Mills >= first && r.Mills < last);
    }

    [Fact]
    public void SampleLoop_TempsAreTheAlgorithmsAndLinkToTheirDevicesSnapshot()
    {
        var run = Simulate();

        run.Temps.Should().NotBeEmpty();
        run.Temps.Should().OnlyContain(t => t.Default == Aid.TempBasalOrigin.Algorithm);
        run.Temps.Should().OnlyContain(t =>
            t.Temp.Origin == Aid.TempBasalOrigin.Algorithm
            && t.Temp.OriginEvidence == Aid.TempBasalOriginEvidence.Snapshot
            && t.Temp.ApsSnapshotId != null);
    }

    [Fact]
    public void SampleLoop_SomeTempsAreCutToMaxSafeBasalAndAtCapIsJudged()
    {
        var run = Simulate();

        var capped = run.Temps.Where(t => t.Source.Rate == run.Steps[0].MaxSafeBasalRate).ToList();
        capped.Should().NotBeEmpty();
        var maxBasal = run.Health.MaxBasal;
        maxBasal.Should().NotBeNull();
        maxBasal!.CapKnown.Percent.Should().BeGreaterThan(0);
        maxBasal.AtCap.Percent.Should().BeGreaterThan(0);
        run.Health.Unknown.MaxBasal.Should().BeNull();
    }

    /// <summary>
    /// A seeded tenant looks current: wherever its range ends, no interruption reaches into the
    /// two hours before the end.
    /// </summary>
    [Fact]
    public void SampleLoop_IsClosedForTheLastTwoHoursOfAnyRange()
    {
        for (var end = new DateTime(2026, 9, 1, 0, 0, 0); end < new DateTime(2026, 10, 1); end = end.AddMinutes(25))
        {
            for (var t = end - DemoLoopInterruptions.QuietBeforeEnd; t < end; t = t.AddMinutes(5))
                DemoLoopInterruptions.StateAt(t, end).Should().Be(DemoLoopState.Closed, $"{t:g} is in the last two hours before {end:g}");
        }
    }

    /// <summary>
    /// A range that ends in what would be a phone gap still ends with the loop closed and a cycle
    /// every five minutes, so the dashboard shows a recent loop status and IOB.
    /// </summary>
    [Fact]
    public void SampleLoop_EndsWithARecentStatus()
    {
        var gap = Enumerable.Range(0, 30)
            .SelectMany(d => DemoLoopInterruptions.On(End.Date.AddDays(-d), DateTime.MaxValue))
            .First(i => i.State == DemoLoopState.PhoneOutOfRange);
        var end = gap.Start.AddMinutes(10);

        var run = Simulate(end);

        var tail = run.Steps.Where(s => s.Time >= end - DemoLoopInterruptions.QuietBeforeEnd).ToList();
        tail.Should().NotBeEmpty().And.OnlyContain(s => s.Loop == DemoLoopState.Closed);
        run.Cycles.Select(c => c.Mills).Should().Contain(tail.Select(s => Mills(s.Time)));
    }

    [Fact]
    public void SampleLoop_IsTheSameOnEveryRun()
    {
        var first = Simulate();
        var second = Simulate();

        second.Temps.Select(t => (t.Source.Mills, t.Source.Rate))
            .Should().Equal(first.Temps.Select(t => (t.Source.Mills, t.Source.Rate)));
        second.Health.Activity.ClosedLoop!.Percent.Should().Be(first.Health.Activity.ClosedLoop!.Percent);
    }
}
