using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class GlucoseWriteEntrySinkTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly DateTime CompletedYearReading = new(2023, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly Mock<IDataEventSink<Entry>> _sink = new();
    private readonly List<Entry> _written = [];
    private readonly SensorGlucoseRepository _sensor;
    private readonly MeterGlucoseRepository _meter;

    public GlucoseWriteEntrySinkTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);
        _context = _db.CreateContext();
        _sink
            .Setup(s => s.OnWrittenAsync(It.IsAny<IReadOnlyList<Entry>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<Entry>, CancellationToken>((entries, _) => _written.AddRange(entries))
            .Returns(Task.CompletedTask);

        var factory = new TestNocturneDbContextFactory(_context);
        _sensor = new SensorGlucoseRepository(
            factory,
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<SensorGlucoseRepository>.Instance,
            entrySink: _sink.Object);
        _meter = new MeterGlucoseRepository(
            factory,
            new Mock<IAuditContext>().Object,
            NullLogger<MeterGlucoseRepository>.Instance,
            entrySink: _sink.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(WriteOrigin.Backfill)]
    [InlineData(WriteOrigin.Live)]
    public async Task SensorGlucoseBulkCreate_TellsTheEntrySinkWhatWasWritten_WhateverTheOrigin(WriteOrigin origin)
    {
        await _sensor.BulkCreateAsync(
            [new SensorGlucose { Mgdl = 120, Timestamp = CompletedYearReading, DataSource = "test-source" }],
            origin);

        _written.Should().ContainSingle().Which.Should().Match<Entry>(
            e => e.Type == "sgv" && e.Date == CompletedYearReading);
    }

    [Fact]
    public async Task MeterGlucoseBulkCreate_TellsTheEntrySinkWhatWasWritten_OnBackfill()
    {
        await _meter.BulkCreateAsync(
            [new MeterGlucose { Mgdl = 110, Timestamp = CompletedYearReading, DataSource = "test-source" }],
            WriteOrigin.Backfill);

        _written.Should().ContainSingle().Which.Should().Match<Entry>(
            e => e.Type == "mbg" && e.Date == CompletedYearReading);
    }

    [Fact]
    public async Task SensorGlucoseBulkCreate_OnBackfill_DoesNotFireTheLiveCreateEvent()
    {
        await _sensor.BulkCreateAsync(
            [new SensorGlucose { Mgdl = 120, Timestamp = CompletedYearReading, DataSource = "test-source" }],
            WriteOrigin.Backfill);

        _sink.Verify(
            s => s.OnCreatedAsync(It.IsAny<IReadOnlyList<Entry>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SensorGlucoseDelete_TellsTheEntrySinkTheDeletedReading()
    {
        var created = await _sensor.CreateAsync(
            new SensorGlucose { Mgdl = 120, Timestamp = CompletedYearReading, DataSource = "test-source" },
            WriteOrigin.Backfill);
        _written.Clear();

        await _sensor.DeleteAsync(created.Id, WriteOrigin.Live);

        _written.Should().Contain(e => e.Type == "sgv" && e.Date == CompletedYearReading);
    }
}
