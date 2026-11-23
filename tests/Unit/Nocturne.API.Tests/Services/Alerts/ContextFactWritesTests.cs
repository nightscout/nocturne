using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The record-to-fact map is complete: every fact the engine can read has a decision on both of its
/// properties, and every V4 record type has a decision on the facts its writes move.
/// </summary>
[Trait("Category", "Unit")]
public class ContextFactWritesTests
{
    /// <summary>Every fact some condition kind reads, as the engine names them.</summary>
    private static IReadOnlySet<string> EngineFacts() =>
        Enum.GetValues<AlertConditionType>()
            .SelectMany(type => RuleTrees.Reads(new AlertRuleSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), "probe", type, "{}", AlertRuleSeverity.Info, "{}", 0,
                AutoResolveEnabled: false, AutoResolveParams: null)))
            .ToHashSet(StringComparer.Ordinal);

    [NativeFact]
    public void EveryFactTheEngineReadsHasADecisionOnAnnouncingAndOnTheClock()
    {
        var facts = EngineFacts();

        facts.Should().NotBeEmpty();
        ContextFactWrites.Facts.Keys.Should().BeEquivalentTo(facts,
            "a fact the engine reads must say whether all its writes announce and whether it moves with the clock");
    }

    [Fact]
    public void EveryV4RecordTypeHasADecisionOnTheFactsItMoves()
    {
        // A device status's extras row is no V4 record, but its write announces itself because it can
        // state the sensor's state.
        var recordTypes = typeof(IV4Record).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                        && typeof(IV4Record).IsAssignableFrom(t))
            .Append(typeof(DeviceStatusExtras));

        ContextFactWrites.MovedBy.Keys.Should().Contain(recordTypes,
            "a record type with no entry would move facts and evaluate nothing");
    }

    [Fact]
    public void EveryMappedFactIsAFact() =>
        ContextFactWrites.MovedBy.Values.SelectMany(f => f).Should().OnlyContain(f => ContextFactWrites.Facts.ContainsKey(f));

    [Fact]
    public void AFactWhoseWritesAllAnnounceIsMovedBySomeRecordType()
    {
        var moved = ContextFactWrites.MovedBy.Values.SelectMany(f => f).ToHashSet();

        ContextFactWrites.Facts.Where(f => f.Value.EveryWriteAnnounces).Select(f => f.Key)
            .Should().OnlyContain(f => moved.Contains(f));
    }

    [Fact]
    public void TheSweepMayWaitOnlyForGlucoseApsUploaderTransmitterTheSensorStateAndTheDetermination() =>
        ContextFactWrites.Facts.Keys.Where(ContextFactWrites.SweepMayWaitFor)
            .Should().BeEquivalentTo(
                [ContextFactWrites.Glucose, ContextFactWrites.Aps, ContextFactWrites.Uploader, ContextFactWrites.Transmitter,
                 ContextFactWrites.SensorState, ContextFactWrites.ApsDetermination]);

    [Fact]
    public void OnlyAnApsSnapshotWriteMovesTheDetermination() =>
        ContextFactWrites.MovedBy.Where(kv => kv.Value.Contains(ContextFactWrites.ApsDetermination)).Select(kv => kv.Key)
            .Should().BeEquivalentTo([typeof(ApsSnapshot)]);

    [Fact]
    public void AnExtrasWriteMovesOnlyTheSensorState() =>
        ContextFactWrites.MovedIn(RecordsChanged.Written([typeof(DeviceStatusExtras)])).Facts
            .Should().BeEquivalentTo([ContextFactWrites.SensorState]);

    [Theory]
    [InlineData(ContextFactWrites.Insulin)]
    [InlineData(ContextFactWrites.Carbs)]
    [InlineData(ContextFactWrites.Pump)]
    [InlineData(ContextFactWrites.Predictions)]
    [InlineData(ContextFactWrites.TimeZone)]
    public void AFactThatMovesWithTheClockIsNeverWaitedFor(string fact)
    {
        ContextFactWrites.Facts[fact].MovesWithClock.Should().BeTrue();
        ContextFactWrites.SweepMayWaitFor(fact).Should().BeFalse();
    }

    [Theory]
    [InlineData(ContextFactWrites.StateSpans)]
    [InlineData(ContextFactWrites.Trackers)]
    [InlineData(ContextFactWrites.Sleep)]
    [InlineData(ContextFactWrites.ActiveAlerts)]
    [InlineData(ContextFactWrites.DoNotDisturb)]
    [InlineData(ContextFactWrites.DeviceAges)]
    public void AFactWithAWritePathThatDoesNotAnnounceIsNeverWaitedFor(string fact)
    {
        ContextFactWrites.Facts[fact].EveryWriteAnnounces.Should().BeFalse();
        ContextFactWrites.SweepMayWaitFor(fact).Should().BeFalse();
    }

    [Fact]
    public void AnUnknownFactIsNeverWaitedFor() =>
        ContextFactWrites.SweepMayWaitFor("no_such_fact").Should().BeFalse();

    [Fact]
    public void ABatchMovesTheUnionOfItsRecordTypesFacts()
    {
        var batch = new RecordsChanged(
        [
            new RecordTypeChange(typeof(SensorGlucose), WriteOrigin.Live, [new SensorGlucose()], [], []),
            new RecordTypeChange(typeof(Bolus), WriteOrigin.Backfill, [], [], []),
            new RecordTypeChange(typeof(StateSpan), WriteOrigin.Live, [], [], []),
        ]);

        ContextFactWrites.MovedIn(batch).Facts.Should().BeEquivalentTo(
            [ContextFactWrites.Glucose, ContextFactWrites.SensorState, ContextFactWrites.Insulin, ContextFactWrites.Pump, ContextFactWrites.Predictions]);
    }

    [Fact]
    public void AWriteOfATypeThatMovesNoFactMovesNothing() =>
        ContextFactWrites.MovedIn(RecordsChanged.Written([typeof(Note), typeof(MeterGlucose)])).Facts.Should().BeEmpty();
}
