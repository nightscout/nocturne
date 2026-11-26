using FluentAssertions;
using Nocturne.Core.Models.Connectors;
using Xunit;

namespace Nocturne.Core.Models.Tests.Connectors;

[Trait("Category", "Unit")]
public class ConnectorSyncStatusTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    [Fact]
    public void AnErrorLeftByTheLastSyncIsFailing() =>
        ConnectorSyncStatus.Of(false, Now.AddMinutes(-5), Now.AddDays(-1), Now, StaleAfter)
            .Should().Be(ConnectorSyncStatus.Failing);

    [Fact]
    public void NoSuccessfulSyncAndNoErrorIsNeverSynced() =>
        ConnectorSyncStatus.Of(true, null, null, Now, StaleAfter).Should().Be(ConnectorSyncStatus.NeverSynced);

    [Fact]
    public void ASyncOlderThanTheThresholdIsStale() =>
        ConnectorSyncStatus.Of(true, null, Now.AddMinutes(-61), Now, StaleAfter).Should().Be(ConnectorSyncStatus.Stale);

    [Fact]
    public void ASyncWithinTheThresholdIsHealthy() =>
        ConnectorSyncStatus.Of(true, null, Now.AddMinutes(-60), Now, StaleAfter).Should().Be(ConnectorSyncStatus.Healthy);

    [Fact]
    public void UnhealthyWithoutAnErrorTimeFallsBackToTheSync() =>
        ConnectorSyncStatus.Of(false, null, Now.AddMinutes(-5), Now, StaleAfter).Should().Be(ConnectorSyncStatus.Healthy);
}
