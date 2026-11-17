using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="DndWindowRepository"/> over an InMemory store: that the active read agrees with
/// <see cref="DndWindowSnapshot.IsActiveAt"/>, and the manual toggle's supersede rule.
/// </summary>
public class DndWindowModuleTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private readonly DbContextOptions<NocturneDbContext> _options = new DbContextOptionsBuilder<NocturneDbContext>()
        .UseInMemoryDatabase($"dnd_window_repo_{Guid.NewGuid()}")
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;

    private readonly FakeTimeProvider _clock = new(Now);

    private NocturneDbContext Db() => new(_options) { TenantId = Tenant };

    private TestNocturneDbContextFactory Factory() => new(Db());

    private DateTime At(double hours) => Now.UtcDateTime.AddHours(hours);

    private static DndWindowEntity Window(
        DndScope scope, DateTime startedAt, DateTime? endsAt = null, DateTime? clearedAt = null) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Tenant,
        Scope = scope,
        StartedAt = startedAt,
        EndsAt = endsAt,
        ClearedAt = clearedAt,
        CreatedAt = startedAt,
    };

    /// <summary>
    /// The read is the snapshot's rule applied to every stored row, whatever the query filters
    /// on. A window that has started at this instant is active, one that ends at it is not.
    /// </summary>
    [Fact]
    public async Task GetActiveReturnsExactlyTheWindowsTheSnapshotRuleAccepts()
    {
        var rows = new[]
        {
            Window(DndScope.All, At(-2)),
            Window(DndScope.Lows, At(-2), endsAt: At(1)),
            Window(DndScope.Highs, At(0)),
            Window(DndScope.Highs, At(-2), endsAt: At(0)),
            Window(DndScope.Lows, At(-2), endsAt: At(-1)),
            Window(DndScope.All, At(-2), clearedAt: At(-1)),
            Window(DndScope.Lows, At(1), endsAt: At(2)),
            Window(DndScope.All, At(-3), clearedAt: At(0)),
        };
        await using (var seed = Db())
        {
            seed.DndWindows.AddRange(rows);
            await seed.SaveChangesAsync();
        }

        var active = await new DndWindowRepository(Factory(), _clock).GetActiveAsync(scope: null, default);

        var expected = rows
            .Where(w => new DndWindowSnapshot(w.Scope, w.StartedAt, w.EndsAt, w.ClearedAt, w.CreatedAt).IsActiveAt(Now.UtcDateTime))
            .Select(w => w.Id);
        active.Select(w => w.Id).Should().BeEquivalentTo(expected);
        active.Should().HaveCount(3, "the fixture holds three windows in force at the instant");
    }

    [Fact]
    public async Task GetActiveReturnsEarliestStartedFirstAndCanNarrowToAScope()
    {
        var later = Window(DndScope.All, At(-1));
        var earlier = Window(DndScope.All, At(-3));
        var lows = Window(DndScope.Lows, At(-2));
        await using (var seed = Db())
        {
            seed.DndWindows.AddRange(later, earlier, lows);
            await seed.SaveChangesAsync();
        }
        var repo = new DndWindowRepository(Factory(), _clock);

        (await repo.GetActiveAsync(scope: null, default)).Select(w => w.Id)
            .Should().Equal(earlier.Id, lows.Id, later.Id);
        (await repo.GetActiveAsync(DndScope.All, default)).Select(w => w.Id)
            .Should().Equal(earlier.Id, later.Id);
    }

    [Fact]
    public async Task EnsureManualKeepsTheEarliestActiveWindowAndSupersedesTheRest()
    {
        var keep = Window(DndScope.All, At(-3));
        var extra = Window(DndScope.All, At(-1));
        var pending = Window(DndScope.All, At(4), endsAt: At(6));
        await using (var seed = Db())
        {
            seed.DndWindows.AddRange(keep, extra, pending);
            await seed.SaveChangesAsync();
        }

        await new DndWindowRepository(Factory(), _clock).EnsureManualAsync(At(2), default);

        await using var db = Db();
        var kept = await db.DndWindows.SingleAsync(w => w.Id == keep.Id);
        kept.EndsAt.Should().Be(At(2));
        kept.StartedAt.Should().Be(At(-3));
        kept.ClearedAt.Should().BeNull();

        var superseded = await db.DndWindows.SingleAsync(w => w.Id == extra.Id);
        superseded.ClearedAt.Should().Be(Now.UtcDateTime);
        superseded.ClearedBy.Should().Be(DndWindowRepository.SupersededBy);

        var untouched = await db.DndWindows.SingleAsync(w => w.Id == pending.Id);
        untouched.ClearedAt.Should().BeNull("a window not yet started is not active, so the toggle leaves it");
    }

    [Fact]
    public async Task EnsureManualStoresAWebWindowStartingNowWhenNoneIsActive()
    {
        await new DndWindowRepository(Factory(), _clock).EnsureManualAsync(At(1), default);

        await using var db = Db();
        var window = await db.DndWindows.SingleAsync();
        window.Scope.Should().Be(DndScope.All);
        window.StartedAt.Should().Be(Now.UtcDateTime);
        window.EndsAt.Should().Be(At(1));
        window.Source.Should().Be("web");
    }
}
