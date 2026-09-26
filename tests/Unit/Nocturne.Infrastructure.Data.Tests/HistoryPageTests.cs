using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The rule in <see cref="HistoryPage"/>: a page cut inside a millisecond either re-sends that
/// millisecond's rows or, when it holds at least <c>limit</c> rows, never advances the cursor.
/// </summary>
[Trait("Category", "Unit")]
public class HistoryPageTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Base = new(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);

    private readonly NocturneDbContext _context;
    private readonly RecordingLogger _logger = new();

    public HistoryPageTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext($"history_page_{Guid.NewGuid()}");
        _context.TenantId = TenantId;
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task MillisecondWithMoreThanLimitRows_ComesBackInOnePage_AndCursorMovesPastIt()
    {
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.CreateVersion7()).ToList();
        await SeedAsync(ids.Select((id, i) => (Base.AddTicks(i + 1), id)).ToArray());

        var first = await PageAsync(CursorMills(Base.AddMilliseconds(-1)), limit: 3);

        first.Select(e => e.Id).Should().BeEquivalentTo(ids);
        _logger.Messages.Should().ContainSingle(m => m.Level == LogLevel.Warning && m.Text.Contains("millisecond"));

        var cursor = first.Max(e => HistoryPage.ToMilliseconds(e.Timestamp));
        var second = await PageAsync(cursor, limit: 3);

        second.Should().BeEmpty();
    }

    [Fact]
    public async Task TieGroupSplitByLimit_IsDeliveredExactlyOnceAcrossConsecutivePages()
    {
        var tieGroup = new[]
        {
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
        };
        var later = Guid.CreateVersion7();
        // Sub-millisecond offsets that all truncate to the same millisecond.
        await SeedAsync(
            (Base.AddTicks(10), tieGroup[0]),
            (Base.AddTicks(20), tieGroup[1]),
            (Base.AddTicks(30), tieGroup[2]),
            (Base.AddMilliseconds(5), later));

        var delivered = await WalkAsync(limit: 2);

        delivered.Should().BeEquivalentTo(tieGroup.Append(later));
        delivered.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task RowOnMillisecondBoundary_IsNotSkipped()
    {
        var onBoundary = Guid.CreateVersion7();
        var nextMs = Guid.CreateVersion7();
        await SeedAsync(
            (Base, onBoundary),
            (Base.AddMilliseconds(1), nextMs));

        var first = await PageAsync(CursorMills(Base) - 1, limit: 1);
        first.Should().ContainSingle().Which.Id.Should().Be(onBoundary);

        var second = await PageAsync(CursorMills(first[0].Timestamp), limit: 1);
        second.Should().ContainSingle().Which.Id.Should().Be(nextMs);
    }

    private async Task<List<ApsSnapshotEntity>> PageAsync(long cursorMills, int limit) =>
        await HistoryPage.GetAsync(
            _context.ApsSnapshots.AsNoTracking(),
            e => e.Timestamp,
            e => e.Id,
            cursorMills,
            limit,
            _logger,
            nameof(ApsSnapshotEntity),
            CancellationToken.None);

    private async Task<List<Guid>> WalkAsync(int limit)
    {
        var delivered = new List<Guid>();
        var cursor = CursorMills(Base.AddMilliseconds(-1));

        for (var page = 0; page < 10; page++)
        {
            var rows = await PageAsync(cursor, limit);
            if (rows.Count == 0)
                break;

            delivered.AddRange(rows.Select(r => r.Id));
            cursor = rows.Max(e => HistoryPage.ToMilliseconds(e.Timestamp));
        }

        return delivered;
    }

    private async Task SeedAsync(params (DateTime Ts, Guid Id)[] rows)
    {
        foreach (var (ts, id) in rows)
        {
            _context.ApsSnapshots.Add(new ApsSnapshotEntity
            {
                Id = id,
                TenantId = TenantId,
                Timestamp = ts,
                UtcOffset = 0,
                AidAlgorithm = "Loop",
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    private static long CursorMills(DateTime value) => HistoryPage.ToMilliseconds(value);

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));
    }
}
