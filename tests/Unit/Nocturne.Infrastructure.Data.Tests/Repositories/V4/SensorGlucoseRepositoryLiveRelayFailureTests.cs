using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Nocturne.Tests.Shared.Logging;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// The two reads a live write makes on the way to the single-stream surfaces (the canonical stream
/// gate and the tenant's last reading) run after the write has committed. A failure of either, a
/// defect included, is logged at error and must not fail that write or skip the last-reading advance.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "SensorGlucose")]
public class SensorGlucoseRepositoryLiveRelayFailureTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000003");

    public static TheoryData<Exception> Failures => new()
    {
        new NpgsqlException("connection refused"),
        new InvalidOperationException("context disposed"),
        new NullReferenceException(),
    };

    private sealed class Exposed(
        INocturneDbContextFactory contexts, ILogger<SensorGlucoseRepository> logger, IGlucoseReadingBroadcaster? broadcaster = null)
        : SensorGlucoseRepository(
            contexts, Mock.Of<IDuplicateLinker>(), Mock.Of<IAuditContext>(), logger, readingBroadcaster: broadcaster)
    {
        public Task<IReadOnlyList<SensorGlucose>> Gate(IReadOnlyList<SensorGlucose> readings, CancellationToken ct = default) =>
            FilterLegacyProjectionAsync(readings, ct);

        public Task Relay(IReadOnlyList<SensorGlucose> readings, CancellationToken ct = default) =>
            OnLiveVisibleCreatedAsync(readings, ct);
    }

    private sealed class FailsTheLastReadingRead(Exception failure) : DbCommandInterceptor
    {
        public bool Armed { get; set; } = true;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken)
        {
            if (Armed && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("last_reading_at", StringComparison.OrdinalIgnoreCase))
                throw failure;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static INocturneDbContextFactory ContextsThatThrow(Exception failure)
    {
        var contexts = new Mock<INocturneDbContextFactory>();
        contexts.Setup(c => c.CreateAsync(It.IsAny<CancellationToken>())).Throws(failure);
        return contexts.Object;
    }

    private static readonly IReadOnlyList<SensorGlucose> Readings =
        [new SensorGlucose { Id = Guid.CreateVersion7(), Mgdl = 120, Timestamp = DateTime.UtcNow }];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task The_canonical_stream_gate_fails_open_and_logs_at_error(Exception failure)
    {
        var logger = new ListLogger<SensorGlucoseRepository>();

        var shown = await new Exposed(ContextsThatThrow(failure), logger).Gate(Readings);

        shown.Should().BeEquivalentTo(Readings);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception == failure);
    }

    [Fact]
    public async Task The_canonical_stream_gate_lets_the_callers_cancellation_through()
    {
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        var logger = new ListLogger<SensorGlucoseRepository>();

        var gate = () => new Exposed(ContextsThatThrow(new OperationCanceledException(shutdown.Token)), logger)
            .Gate(Readings, shutdown.Token);

        await gate.Should().ThrowAsync<OperationCanceledException>();
        logger.Entries.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task The_relay_is_skipped_and_logged_at_error_when_the_last_reading_cannot_be_read(Exception failure)
    {
        var broadcaster = new Mock<IGlucoseReadingBroadcaster>();
        var logger = new ListLogger<SensorGlucoseRepository>();

        await new Exposed(ContextsThatThrow(failure), logger, broadcaster.Object).Relay(Readings);

        broadcaster.VerifyNoOtherCalls();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception == failure);
    }

    [Fact]
    public async Task The_relay_lets_the_callers_cancellation_through()
    {
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        var broadcaster = new Mock<IGlucoseReadingBroadcaster>();

        var relay = () => new Exposed(
            ContextsThatThrow(new OperationCanceledException(shutdown.Token)), new ListLogger<SensorGlucoseRepository>(), broadcaster.Object)
            .Relay(Readings, shutdown.Token);

        await relay.Should().ThrowAsync<OperationCanceledException>();
        broadcaster.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_failed_relay_read_neither_fails_the_committed_write_nor_stops_the_last_reading_advancing(Exception failure)
    {
        var interceptor = new FailsTheLastReadingRead(failure);
        using var db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, "test", interceptor);
        using var context = db.CreateContext();
        var logger = new ListLogger<SensorGlucoseRepository>();
        var broadcaster = new Mock<IGlucoseReadingBroadcaster>();
        var repository = new Exposed(new TestNocturneDbContextFactory(context), logger, broadcaster.Object);
        var timestamp = DateTime.UtcNow.AddMinutes(-1);

        var created = await repository.BulkCreateAsync(
            [new SensorGlucose { Mgdl = 120, Timestamp = timestamp, DataSource = "test-source" }], WriteOrigin.Live);

        created.Should().ContainSingle();
        broadcaster.VerifyNoOtherCalls();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Exception == failure);
        interceptor.Armed = false;
        context.ChangeTracker.Clear();
        (await context.Tenants.AsNoTracking().SingleAsync(t => t.Id == TenantId)).LastReadingAt.Should().Be(RecordTime.AtMillisecond(timestamp));
    }
}
