using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     Covers the bookkeeping the shared publish path owns for every connector:
///     per-type counts and publish progress.
/// </summary>
public class PublishRecordTypePathTests
{
    private sealed class PublishedRecord;

    private sealed class TestConfig : BaseConnectorConfiguration
    {
        protected override void ValidateSourceSpecificConfiguration() { }
    }

    /// <summary>Captures the messages the shared publish path logs, which no fixture otherwise sees.</summary>
    private sealed class RecordingLogger : ILogger<TestConnectorService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class TestConnectorService : BaseConnectorService<TestConfig>
    {
        private readonly Func<SyncResult, CancellationToken, Task> _syncBody;

        public TestConnectorService(
            Func<SyncResult, CancellationToken, Task> syncBody,
            ILogger<TestConnectorService>? logger = null)
            : base(new HttpClient(),
                new ConnectorServerResolver<TestConfig>(null, null, null),
                logger ?? NullLogger<TestConnectorService>.Instance)
        {
            _syncBody = syncBody;
        }

        protected override string ConnectorSource => "test";
        public override string ServiceName => "Test";

        public override Task<bool> AuthenticateAsync() => Task.FromResult(true);

        protected override async Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request,
            TestConfig config,
            CancellationToken cancellationToken)
        {
            var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };
            await _syncBody(result, cancellationToken);
            return result;
        }

        public Task<bool> PublishAsync(
            SyncResult result,
            SyncDataType dataType,
            HashSet<SyncDataType> activeTypes,
            List<PublishedRecord> records,
            bool publishSucceeds = true,
            CancellationToken cancellationToken = default)
            => PublishRecordTypeAsync(result, dataType, activeTypes, records,
                (_, _, _) => Task.FromResult(publishSucceeds),
                new TestConfig(), cancellationToken);

        public Task<bool> PublishThrowingAsync(
            SyncResult result,
            SyncDataType dataType,
            HashSet<SyncDataType> activeTypes,
            List<PublishedRecord> records)
            => PublishRecordTypeAsync(result, dataType, activeTypes, records,
                (_, _, _) => throw new InvalidOperationException("publisher exploded"),
                new TestConfig(), CancellationToken.None);
    }

    private static Task<SyncResult> RunAsync(
        Func<TestConnectorService, SyncResult, Task> body,
        ISyncProgressReporter? reporter = null)
    {
        TestConnectorService? service = null;
        service = new TestConnectorService((result, _) => body(service!, result));
        return service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            new TestConfig(),
            CancellationToken.None,
            reporter);
    }

    [Fact]
    public async Task InactiveType_RecordsNothing()
    {
        // Arrange: the tenant has the type switched off
        var active = new HashSet<SyncDataType> { SyncDataType.Boluses };

        // Act
        var result = await RunAsync((service, syncResult) =>
            service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new PublishedRecord()]));

        // Assert
        result.ItemsSynced.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyBatch_RecordsAnExplicitZero()
    {
        // Arrange: the tenant's sync card renders a badge per key, so an active type that came back
        // empty has to say zero — a missing key reads as "never checked".
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };

        // Act
        var result = await RunAsync((service, syncResult) =>
            service.PublishAsync(syncResult, SyncDataType.Glucose, active, []));

        // Assert
        result.Success.Should().BeTrue(
            "finding nothing is not a failure: reporting one would turn every quiet but enabled "
            + "type red and freeze the connector's last successful sync");
        result.ItemsSynced.Should().Equal(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 0,
        });
    }

    [Fact]
    public async Task EmptyBatchAfterAPublishedOne_LeavesTheCountAlone()
    {
        // Arrange: a paginated crawl ends on an empty page, which must not erase what it landed
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };

        // Act
        var result = await RunAsync(async (service, syncResult) =>
        {
            await service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new PublishedRecord(), new PublishedRecord()]);
            await service.PublishAsync(syncResult, SyncDataType.Glucose, active, []);
        });

        // Assert
        result.ItemsSynced[SyncDataType.Glucose].Should().Be(2);
    }

    [Fact]
    public async Task FailedPublish_StillCountsTheBatch()
    {
        // Arrange: ItemsSynced reports what the sync handed to the publisher, whatever the
        // publisher then did with it.
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };

        // Act
        var result = await RunAsync((service, syncResult) =>
            service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new PublishedRecord()], publishSucceeds: false));

        // Assert
        result.Success.Should().BeFalse();
        result.ItemsSynced[SyncDataType.Glucose].Should().Be(1);
    }

    [Fact]
    public async Task SuccessLog_CoversThePublishedBatchAndNotTheEmptyOne()
    {
        // Arrange: the empty path records its zero without logging, so a run over quiet types does
        // not fill the log with "Synced 0" lines it has nothing to say about.
        var logger = new RecordingLogger();
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose, SyncDataType.Boluses };
        var service = new TestConnectorService((_, _) => Task.CompletedTask, logger);
        var result = new SyncResult { Success = true };

        // Act
        await service.PublishAsync(result, SyncDataType.Glucose, active,
            [new PublishedRecord(), new PublishedRecord()]);
        await service.PublishAsync(result, SyncDataType.Boluses, active, []);

        // Assert
        logger.Messages.Should().ContainSingle(m => m.Contains("Synced"))
            .Which.Should().Contain("Synced 2 Glucose records");
    }

    [Fact]
    public async Task SuccessLog_IsSuppressedForARejectedPublish()
    {
        // Arrange: the log says "Synced", so it must not appear for a batch the publisher refused —
        // a triage reading it would take the records for stored.
        var logger = new RecordingLogger();
        var active = new HashSet<SyncDataType> { SyncDataType.Boluses };
        var service = new TestConnectorService((_, _) => Task.CompletedTask, logger);
        var result = new SyncResult { Success = true };

        // Act
        await service.PublishAsync(result, SyncDataType.Boluses, active,
            [new PublishedRecord(), new PublishedRecord()], publishSucceeds: false);

        // Assert
        result.ItemsSynced[SyncDataType.Boluses].Should().Be(2, "the batch still reached the publisher");
        logger.Messages.Should().NotContain(m => m.Contains("Synced"));
    }

    [Fact]
    public async Task ThrowingPublish_RecordsNoCount()
    {
        // Arrange: the count is recorded once the publish has returned, so a batch whose publish
        // threw is not reported as handed over — unlike one the publisher rejected, which is.
        // Connectors catch the throw and report it as a sync error instead.
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };
        var result = new SyncResult { Success = true };
        var service = new TestConnectorService((_, _) => Task.CompletedTask);

        // Act
        var publish = () => service.PublishThrowingAsync(result, SyncDataType.Glucose, active,
            [new PublishedRecord()]);

        // Assert
        await publish.Should().ThrowAsync<InvalidOperationException>();
        result.ItemsSynced.Should().BeEmpty();
    }

    [Fact]
    public async Task ReturnValue_IsTrueOnlyForAnAcceptedPublish()
    {
        // Arrange: connectors advance a cursor on the return value — CareLink's alarm dedup key
        // holds an alarm the tenant never took, whatever the reason it did not take it.
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };
        var outcomes = new List<bool>();

        // Act: gated-off type, empty batch, rejected publish, accepted publish
        await RunAsync(async (service, syncResult) =>
        {
            outcomes.Add(await service.PublishAsync(syncResult, SyncDataType.Boluses, active,
                [new PublishedRecord()]));
            outcomes.Add(await service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                []));
            outcomes.Add(await service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new PublishedRecord()], publishSucceeds: false));
            outcomes.Add(await service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new PublishedRecord()]));
        });

        // Assert
        outcomes.Should().Equal(false, false, false, true);
    }

    [Fact]
    public async Task Progress_EmitsPublishingDataTypePerPublishedBatch()
    {
        // Arrange
        var reported = new List<SyncProgressEvent>();
        var reporter = new Mock<ISyncProgressReporter>();
        reporter
            .Setup(r => r.ReportProgressAsync(It.IsAny<SyncProgressEvent>(), It.IsAny<CancellationToken>()))
            .Callback<SyncProgressEvent, CancellationToken>((e, _) => reported.Add(e))
            .Returns(Task.CompletedTask);

        var active = new HashSet<SyncDataType> { SyncDataType.Glucose, SyncDataType.Boluses };

        // Act
        await RunAsync(async (service, syncResult) =>
        {
            await service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new PublishedRecord(), new PublishedRecord()]);
            await service.PublishAsync(syncResult, SyncDataType.Boluses, active,
                [new PublishedRecord()]);
        }, reporter.Object);

        // Assert: the run's own terminal message is not a publish message
        var publishes = reported.Where(e => e.MessageType == SyncMessageType.PublishingDataType).ToList();
        publishes.Should().HaveCount(2);
        publishes.Should().OnlyContain(e => e.Phase == SyncPhase.Syncing && e.ConnectorId == "test");
        publishes[0].MessageParams.Should().Contain("dataType", nameof(SyncDataType.Glucose))
            .And.Contain("count", "2");
        publishes[1].MessageParams.Should().Contain("dataType", nameof(SyncDataType.Boluses))
            .And.Contain("count", "1");
    }

    [Fact]
    public async Task Progress_SkippedPublishEmitsNothing()
    {
        // Arrange
        var reporter = new Mock<ISyncProgressReporter>();
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };

        // Act: one gated-off type and one empty batch
        await RunAsync(async (service, syncResult) =>
        {
            await service.PublishAsync(syncResult, SyncDataType.Boluses, active,
                [new PublishedRecord()]);
            await service.PublishAsync(syncResult, SyncDataType.Glucose, active, []);
        }, reporter.Object);

        // Assert
        reporter.Verify(
            r => r.ReportProgressAsync(
                It.Is<SyncProgressEvent>(e => e.MessageType == SyncMessageType.PublishingDataType),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Progress_NoReporter_PublishesWithoutReporting()
    {
        // Arrange
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };

        // Act
        var result = await RunAsync((service, syncResult) =>
            service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new PublishedRecord()]));

        // Assert
        result.Success.Should().BeTrue();
        result.ItemsSynced[SyncDataType.Glucose].Should().Be(1);
    }

    [Fact]
    public async Task Progress_NotCarriedAcrossRuns()
    {
        // Arrange: the reporter is per-run state on a per-run service instance, so a publish
        // outside a sync run must not reach the previous run's reporter.
        var reporter = new Mock<ISyncProgressReporter>();
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };
        TestConnectorService? captured = null;

        await RunAsync((service, syncResult) =>
        {
            captured = service;
            return Task.CompletedTask;
        }, reporter.Object);

        // Act
        await captured!.PublishAsync(new SyncResult(), SyncDataType.Glucose, active,
            [new PublishedRecord()]);

        // Assert
        reporter.Verify(
            r => r.ReportProgressAsync(
                It.Is<SyncProgressEvent>(e => e.MessageType == SyncMessageType.PublishingDataType),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
