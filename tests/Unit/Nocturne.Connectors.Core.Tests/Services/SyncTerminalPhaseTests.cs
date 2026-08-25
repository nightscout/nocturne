using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     Covers what the shared run wrapper says about a finished sync: the terminal progress message
///     every run ends with, and the failure message the tenant's sync card headlines. A run that
///     never reports a terminal <see cref="SyncPhase"/> leaves the tenant's connector stuck on
///     "syncing" until the page is reloaded, and one that reports no message headlines as an
///     unexplained failure — so both are owned by the wrapper rather than left to each connector.
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

        public int AuthenticateCalls { get; private set; }
        public int EnsureAuthenticatedCalls { get; private set; }

        protected override string ConnectorSource => "test";
        public override string ServiceName => "Test";

        public override Task<bool> AuthenticateAsync()
        {
            AuthenticateCalls++;
            return Task.FromResult(AuthenticationSucceeds);
        }

        protected override Task<bool> EnsureAuthenticatedAsync(
            TestConfig config,
            CancellationToken cancellationToken)
        {
            EnsureAuthenticatedCalls++;
            return Task.FromResult(AuthenticationSucceeds);
        }

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
    public async Task RequestedSync_AuthenticationFailure_ReportsOneFailedPhaseAndSkipsTheSync()
    {
        // Arrange: the guard runs inside the run wrapper, so a rejected credential on the
        // requested-range entry point still resolves the tenant's in-progress indicator.
        var (reporter, reported) = BuildReporter();
        var syncRan = false;
        var service = new TestConnectorService(_ =>
        {
            syncRan = true;
            return Task.CompletedTask;
        }) { AuthenticationSucceeds = false };

        // Act
        var result = await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            new TestConfig(),
            CancellationToken.None,
            reporter.Object);

        // Assert
        result.Success.Should().BeFalse();
        syncRan.Should().BeFalse();
        reported.Should().ContainSingle().Which.Phase.Should().Be(SyncPhase.Failed);
        reported[0].ErrorMessage.Should().Be("Authentication failed for test");
    }

    [Fact]
    public async Task RequestedSync_AuthenticatesThroughTheGuardOnly()
    {
        // Arrange
        var service = new TestConnectorService(_ => Task.CompletedTask);

        // Act
        await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, new TestConfig(), CancellationToken.None);

        // Assert
        service.EnsureAuthenticatedCalls.Should().Be(1);
        service.AuthenticateCalls.Should().Be(0);
    }

    [Fact]
    public async Task BackgroundSync_AuthenticatesOnceAndNotThroughTheGuard()
    {
        // Arrange: the background entry point owns its own hand-shake, so a connector overriding
        // both hooks must not authenticate twice for one run.
        var service = new TestConnectorService(_ => Task.CompletedTask);

        // Act
        await service.SyncDataAsync(new TestConfig(), CancellationToken.None);

        // Assert
        service.AuthenticateCalls.Should().Be(1);
        service.EnsureAuthenticatedCalls.Should().Be(0);
    }

    [Fact]
    public async Task FailedSyncRecordingOnlyErrors_HeadlinesTheFirstOne()
    {
        // Arrange: the shape a per-type catch block leaves behind — the type that failed is in the
        // errors and nothing has named the run.
        var (reporter, _) = BuildReporter();

        // Act
        var result = await RunAsync(result =>
        {
            result.Success = false;
            result.Errors.Add("Failed to sync Boluses: upstream refused the request");
            result.Errors.Add("Failed to sync Notes: upstream refused the request");
            return Task.CompletedTask;
        }, reporter.Object);

        // Assert
        result.Message.Should().Be("Failed to sync Boluses: upstream refused the request",
            "the card headlines the message, so leaving it empty hides which type failed behind a "
            + "generic fallback");
    }

    [Fact]
    public async Task FailedSync_KeepsTheMessageTheRunAlreadyChose()
    {
        // Arrange: a summary an inner path chose says more than the raw error text does
        var (reporter, _) = BuildReporter();

        // Act
        var result = await RunAsync(result =>
        {
            result.Success = false;
            result.Message = "Sync failed while fetching data";
            result.Errors.Add("Chunk 2/5 failed (2026-01-08 to 2026-01-15)");
            return Task.CompletedTask;
        }, reporter.Object);

        // Assert
        result.Message.Should().Be("Sync failed while fetching data");
    }

    [Fact]
    public async Task AuthenticationFailure_KeepsItsOwnSummary()
    {
        // Arrange: the authentication result carries both halves already, and its message is the
        // one the tenant can act on — not the source-qualified detail meant for the error list.
        var (reporter, _) = BuildReporter();
        var service = new TestConnectorService(_ => Task.CompletedTask) { AuthenticationSucceeds = false };

        // Act
        var result = await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            new TestConfig(),
            CancellationToken.None,
            reporter.Object);

        // Assert
        result.Message.Should().Be("Authentication failed");
    }

    [Fact]
    public async Task BackgroundSyncThatThrew_HeadlinesTheExceptionItSwallowed()
    {
        // Arrange: unlike the requested-range entry point, the background one converts an escaped
        // exception into an errors-only result of its own, which no connector code can name.
        var (reporter, _) = BuildReporter();
        var service = new TestConnectorService(_ => throw new InvalidOperationException("upstream went away"));

        // Act
        var result = await service.SyncDataAsync(new TestConfig(), CancellationToken.None, null, reporter.Object);

        // Assert
        result.Success.Should().BeFalse();
        result.Message.Should().Be("upstream went away");
    }

    [Fact]
    public async Task SuccessfulSyncCarryingAnError_IsNotHeadlinedAsAFailure()
    {
        // Arrange: the card falls back to its own success line when the message is empty, so a
        // run that stayed green must not have an error text stood in for it.
        var (reporter, _) = BuildReporter();

        // Act
        var result = await RunAsync(result =>
        {
            result.Errors.Add("Failed to sync Notes: upstream refused the request");
            return Task.CompletedTask;
        }, reporter.Object);

        // Assert
        result.Success.Should().BeTrue();
        result.Message.Should().BeEmpty();
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
