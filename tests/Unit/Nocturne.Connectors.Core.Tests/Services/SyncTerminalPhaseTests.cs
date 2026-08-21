using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     Covers the terminal progress message every sync ends with. A run that never reports a
///     terminal <see cref="SyncPhase"/> leaves the tenant's connector stuck on "syncing" until the
///     page is reloaded, so the outcome is reported by the shared run wrapper rather than left to
///     each connector.
/// </summary>
public class SyncTerminalPhaseTests
{
    private sealed class TestConfig : BaseConnectorConfiguration
    {
        protected override void ValidateSourceSpecificConfiguration() { }
    }

    private sealed class TestConnectorService : BaseConnectorService<TestConfig>
    {
        private readonly Func<SyncResult, Task> _syncBody;

        public TestConnectorService(Func<SyncResult, Task> syncBody)
            : base(new HttpClient(),
                new ConnectorServerResolver<TestConfig>(null, null, null),
                NullLogger<TestConnectorService>.Instance)
        {
            _syncBody = syncBody;
        }

        public bool AuthenticationSucceeds { get; init; } = true;

        protected override string ConnectorSource => "test";
        public override string ServiceName => "Test";

        public override Task<bool> AuthenticateAsync() => Task.FromResult(AuthenticationSucceeds);

        protected override async Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request,
            TestConfig config,
            CancellationToken cancellationToken)
        {
            var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };
            await _syncBody(result);
            return result;
        }
    }

    private static (Mock<ISyncProgressReporter> Reporter, List<SyncProgressEvent> Reported) BuildReporter()
    {
        var reported = new List<SyncProgressEvent>();
        var reporter = new Mock<ISyncProgressReporter>();
        reporter
            .Setup(r => r.ReportProgressAsync(It.IsAny<SyncProgressEvent>(), It.IsAny<CancellationToken>()))
            .Callback<SyncProgressEvent, CancellationToken>((e, _) => reported.Add(e))
            .Returns(Task.CompletedTask);
        return (reporter, reported);
    }

    private static Task<SyncResult> RunAsync(
        Func<SyncResult, Task> body,
        ISyncProgressReporter reporter,
        bool authenticationSucceeds = true)
        => new TestConnectorService(body) { AuthenticationSucceeds = authenticationSucceeds }
            .SyncDataAsync(
                new SyncRequest { DataTypes = [SyncDataType.Glucose] },
                new TestConfig(),
                CancellationToken.None,
                reporter);

    [Fact]
    public async Task SuccessfulSync_ReportsCompletedPhaseWithNoErrorMessage()
    {
        // Arrange
        var (reporter, reported) = BuildReporter();

        // Act
        await RunAsync(_ => Task.CompletedTask, reporter.Object);

        // Assert
        reported.Should().HaveCount(1);
        reported[0].MessageType.Should().Be(SyncMessageType.SyncComplete);
        reported[0].Phase.Should().Be(SyncPhase.Completed);
        reported[0].ErrorMessage.Should().BeNull();
        reported[0].ConnectorId.Should().Be("test");
    }

    [Fact]
    public async Task FailedSync_ReportsFailedPhaseCarryingTheRecordedErrors()
    {
        // Arrange
        var (reporter, reported) = BuildReporter();

        // Act
        await RunAsync(result =>
        {
            result.Success = false;
            result.Message = "Sync failed with exception";
            result.Errors.Add("Glucose publish failed");
            result.Errors.Add("Boluses publish failed");
            return Task.CompletedTask;
        }, reporter.Object);

        // Assert: the errors are what went wrong; the summary message is the coarser fallback
        reported.Should().HaveCount(1);
        reported[0].MessageType.Should().Be(SyncMessageType.SyncFailed);
        reported[0].Phase.Should().Be(SyncPhase.Failed);
        reported[0].ErrorMessage.Should().Be("Glucose publish failed; Boluses publish failed");
    }

    [Fact]
    public async Task FailedSyncWithNoRecordedErrors_FallsBackToTheResultMessage()
    {
        // Arrange
        var (reporter, reported) = BuildReporter();

        // Act
        await RunAsync(result =>
        {
            result.Success = false;
            result.Message = "Authentication failed";
            return Task.CompletedTask;
        }, reporter.Object);

        // Assert
        reported.Should().ContainSingle().Which.ErrorMessage.Should().Be("Authentication failed");
    }

    [Fact]
    public async Task FailedSyncWithNothingToSay_ReportsFailedWithoutAnErrorMessage()
    {
        // Arrange: a connector that flags failure but records neither errors nor a message
        var (reporter, reported) = BuildReporter();

        // Act
        await RunAsync(result =>
        {
            result.Success = false;
            return Task.CompletedTask;
        }, reporter.Object);

        // Assert
        reported.Should().ContainSingle().Which.Phase.Should().Be(SyncPhase.Failed);
        reported[0].ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ThrowingSync_ReportsFailedPhaseAndRethrows()
    {
        // Arrange: a connector that lets an exception escape still has to release the UI
        var (reporter, reported) = BuildReporter();

        // Act
        var act = () => RunAsync(_ => throw new InvalidOperationException("upstream went away"), reporter.Object);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        reported.Should().ContainSingle().Which.Phase.Should().Be(SyncPhase.Failed);
        reported[0].ErrorMessage.Should().Be("upstream went away");
    }

    [Fact]
    public async Task CancelledSync_ReportsNothing()
    {
        // Arrange: a run the caller withdrew has no outcome to report.
        var (reporter, reported) = BuildReporter();

        // Act
        var act = () => RunAsync(_ => throw new OperationCanceledException(), reporter.Object);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        reported.Should().BeEmpty();
    }

    [Fact]
    public async Task BackgroundSync_AuthenticationFailure_ReportsFailedPhase()
    {
        // Arrange: the background entry point authenticates before any connector code runs, and a
        // rejected credential is the commonest way a sync ends without fetching anything.
        var (reporter, reported) = BuildReporter();
        var service = new TestConnectorService(_ => Task.CompletedTask) { AuthenticationSucceeds = false };

        // Act
        var result = await service.SyncDataAsync(new TestConfig(), CancellationToken.None, null, reporter.Object);

        // Assert
        result.Success.Should().BeFalse();
        reported.Should().ContainSingle().Which.Phase.Should().Be(SyncPhase.Failed);
        reported[0].ErrorMessage.Should().Be("Authentication failed for test");
    }

    [Fact]
    public async Task BackgroundSync_Success_ReportsCompletedPhaseOnce()
    {
        // Arrange
        var (reporter, reported) = BuildReporter();
        var service = new TestConnectorService(_ => Task.CompletedTask);

        // Act
        await service.SyncDataAsync(new TestConfig(), CancellationToken.None, null, reporter.Object);

        // Assert: one wrapper owns the outcome, so the two entry points cannot double-report
        reported.Should().ContainSingle().Which.Phase.Should().Be(SyncPhase.Completed);
    }

    [Fact]
    public async Task NoReporter_SyncStillCompletes()
    {
        // Arrange
        var service = new TestConnectorService(_ => Task.CompletedTask);

        // Act
        var result = await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, new TestConfig(), CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
    }
}
