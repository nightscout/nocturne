using FluentAssertions;
using Nocturne.API.Services;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Migration;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A job in a live state is cancellable and is orphaned by a restart; every other state is settled.
/// The definition is read from one place, so these pin it rather than any caller of it.
/// </summary>
public class MigrationJobStatesTests
{
    [Theory]
    [InlineData(MigrationJobState.Pending, true)]
    [InlineData(MigrationJobState.Running, true)]
    [InlineData(MigrationJobState.Completed, false)]
    [InlineData(MigrationJobState.Failed, false)]
    [InlineData(MigrationJobState.Cancelled, false)]
    [InlineData(MigrationJobState.Interrupted, false)]
    public void IsLive_ClassifiesEachState(MigrationJobState state, bool expected) =>
        state.IsLive().Should().Be(expected);

    [Fact]
    public void EveryState_IsClassified() =>
        Enum.GetNames<MigrationJobState>().Should().BeEquivalentTo(
            "Pending", "Running", "Completed", "Failed", "Cancelled", "Interrupted");

    [Fact]
    public void LiveNames_AreExactlyTheLiveStates() =>
        MigrationJobStates.LiveNames.Should().BeEquivalentTo("Pending", "Running");

    [Fact]
    public void WireValues_StayStable()
    {
        ((int)MigrationJobState.Pending).Should().Be(0);
        ((int)MigrationJobState.Running).Should().Be(2);
        ((int)MigrationJobState.Completed).Should().Be(3);
        ((int)MigrationJobState.Failed).Should().Be(4);
        ((int)MigrationJobState.Cancelled).Should().Be(5);
        ((int)MigrationJobState.Interrupted).Should().Be(6);
    }

    [Theory]
    [InlineData(ConnectorResetJobState.Pending, true)]
    [InlineData(ConnectorResetJobState.Running, true)]
    [InlineData(ConnectorResetJobState.Completed, false)]
    [InlineData(ConnectorResetJobState.Failed, false)]
    [InlineData(ConnectorResetJobState.Cancelled, false)]
    [InlineData(ConnectorResetJobState.Interrupted, false)]
    public void ConnectorReset_IsLive_ClassifiesEachState(ConnectorResetJobState state, bool expected) =>
        state.IsLive().Should().Be(expected);

    [Fact]
    public void RestartSweep_ReadsTheLiveNamesOfEachJobTable()
    {
        BackgroundJobLiveStates.ByTable.Should().HaveCount(2);
        BackgroundJobLiveStates.ByTable["migration_runs"].Should().BeEquivalentTo(MigrationJobStates.LiveNames);
        BackgroundJobLiveStates.ByTable["connector_reset_jobs"].Should().BeEquivalentTo(ConnectorResetJobStates.LiveNames);
    }
}
