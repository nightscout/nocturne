using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     Covers the bookkeeping the shared publish path owns for every connector:
///     <see cref="SyncResult.LastEntryTimes"/> and per-type publish progress.
/// </summary>
public class PublishRecordTypePathTests
{
    private sealed class TimedRecord
    {
        public DateTime? At { get; init; }
    }

    private sealed class TestConfig : BaseConnectorConfiguration
    {
        protected override void ValidateSourceSpecificConfiguration() { }
    }

    private sealed class TestConnectorService : BaseConnectorService<TestConfig>
    {
        private readonly Func<SyncResult, CancellationToken, Task> _syncBody;

        public TestConnectorService(Func<SyncResult, CancellationToken, Task> syncBody)
            : base(new HttpClient(),
                new ConnectorServerResolver<TestConfig>(null, null, null),
                NullLogger<TestConnectorService>.Instance)
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
            List<TimedRecord> records,
            Func<TimedRecord, DateTime?>? timestampOf,
            bool publishSucceeds = true,
            CancellationToken cancellationToken = default)
            => PublishRecordTypeAsync(result, dataType, activeTypes, records,
                (_, _, _) => Task.FromResult(publishSucceeds),
                new TestConfig(), cancellationToken, timestampOf: timestampOf);
    }

    private static readonly DateTime Older = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

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
    public async Task LastEntryTimes_LaterBatchWithOlderMax_DoesNotRegressTheValue()
    {
        // Arrange: two batches for the same type, the newer data published first — the order a
        // paginated or chunked crawl can easily produce.
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };

        // Act
        var result = await RunAsync(async (service, syncResult) =>
        {
            await service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new TimedRecord { At = Newer }], r => r.At);
            await service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new TimedRecord { At = Older }], r => r.At);
        });

        // Assert: max-compare, not assignment
        result.LastEntryTimes[SyncDataType.Glucose].Should().Be(Newer);
    }

    [Fact]
    public async Task LastEntryTimes_LaterBatchWithNewerMax_RaisesTheValue()
    {
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };

        var result = await RunAsync(async (service, syncResult) =>
        {
            await service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new TimedRecord { At = Older }], r => r.At);
            await service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new TimedRecord { At = Newer }], r => r.At);
        });

        result.LastEntryTimes[SyncDataType.Glucose].Should().Be(Newer);
    }

    [Fact]
    public async Task LastEntryTimes_ExistingNullValue_IsRaised()
    {
        // Arrange: lifted comparison against null is false, so a null-valued entry would survive
        // a plain `newest > existing` guard forever.
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };

        // Act
        var result = await RunAsync(async (service, syncResult) =>
        {
            syncResult.LastEntryTimes[SyncDataType.Glucose] = null;
            await service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new TimedRecord { At = Older }], r => r.At);
        });

        // Assert
        result.LastEntryTimes[SyncDataType.Glucose].Should().Be(Older);
    }

    [Fact]
    public async Task LastEntryTimes_NoSelector_RecordsNothing()
    {
        // Arrange: a record type that carries no time opts out by passing no selector.
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };

        // Act
        var result = await RunAsync((service, syncResult) =>
            service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new TimedRecord { At = Newer }], timestampOf: null));

        // Assert: the batch still counts, it just has no time to report
        result.ItemsSynced[SyncDataType.Glucose].Should().Be(1);
        result.LastEntryTimes.Should().NotContainKey(SyncDataType.Glucose);
    }

    [Fact]
    public async Task LastEntryTimes_SelectorReturnsNullForEveryRecord_RecordsNothing()
    {
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };

        var result = await RunAsync((service, syncResult) =>
            service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new TimedRecord { At = null }], r => r.At));

        result.LastEntryTimes.Should().NotContainKey(SyncDataType.Glucose);
    }

    [Fact]
    public async Task InactiveType_RecordsNothing()
    {
        // Arrange: the tenant has the type switched off
        var active = new HashSet<SyncDataType> { SyncDataType.Boluses };

        // Act
        var result = await RunAsync((service, syncResult) =>
            service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new TimedRecord { At = Newer }], r => r.At));

        // Assert
        result.LastEntryTimes.Should().BeEmpty();
        result.ItemsSynced.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyBatch_RecordsNothing()
    {
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };

        var result = await RunAsync((service, syncResult) =>
            service.PublishAsync(syncResult, SyncDataType.Glucose, active, [], r => r.At));

        result.LastEntryTimes.Should().BeEmpty();
        result.ItemsSynced.Should().BeEmpty();
    }

    [Fact]
    public async Task FailedPublish_StillRecordsLastEntryTime()
    {
        // Arrange: LastEntryTimes reports what the sync saw, matching ItemsSynced, which the same
        // helper already counts regardless of publish outcome.
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };

        // Act
        var result = await RunAsync((service, syncResult) =>
            service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new TimedRecord { At = Newer }], r => r.At, publishSucceeds: false));

        // Assert
        result.Success.Should().BeFalse();
        result.LastEntryTimes[SyncDataType.Glucose].Should().Be(Newer);
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
                [new TimedRecord { At = Newer }], r => r.At));
            outcomes.Add(await service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [], r => r.At));
            outcomes.Add(await service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new TimedRecord { At = Newer }], r => r.At, publishSucceeds: false));
            outcomes.Add(await service.PublishAsync(syncResult, SyncDataType.Glucose, active,
                [new TimedRecord { At = Newer }], r => r.At));
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
                [new TimedRecord { At = Newer }, new TimedRecord { At = Older }], r => r.At);
            await service.PublishAsync(syncResult, SyncDataType.Boluses, active,
                [new TimedRecord { At = Newer }], r => r.At);
        }, reporter.Object);

        // Assert
        reported.Should().HaveCount(2);
        reported.Should().OnlyContain(e => e.MessageType == SyncMessageType.PublishingDataType
                                        && e.Phase == SyncPhase.Syncing
                                        && e.ConnectorId == "test");
        reported[0].MessageParams.Should().Contain("dataType", nameof(SyncDataType.Glucose))
            .And.Contain("count", "2");
        reported[1].MessageParams.Should().Contain("dataType", nameof(SyncDataType.Boluses))
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
                [new TimedRecord { At = Newer }], r => r.At);
            await service.PublishAsync(syncResult, SyncDataType.Glucose, active, [], r => r.At);
        }, reporter.Object);

        // Assert
        reporter.Verify(
            r => r.ReportProgressAsync(It.IsAny<SyncProgressEvent>(), It.IsAny<CancellationToken>()),
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
                [new TimedRecord { At = Newer }], r => r.At));

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
            [new TimedRecord { At = Newer }], r => r.At);

        // Assert
        reporter.Verify(
            r => r.ReportProgressAsync(It.IsAny<SyncProgressEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
