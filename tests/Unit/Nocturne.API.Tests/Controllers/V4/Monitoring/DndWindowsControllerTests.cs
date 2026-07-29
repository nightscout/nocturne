using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Unit coverage for <see cref="DndWindowsController"/> (ADR 0004 D5): idempotent create,
/// one-active-window-per-scope supersede, resolve-on-read for the active list, and idempotent
/// clear. End-to-end behaviour against real Postgres is pinned by the Aspire integration tests.
/// </summary>
[Trait("Category", "Unit")]
public class DndWindowsControllerTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTime Now = DateTime.UtcNow;

    [Fact]
    public async Task Create_thenGetActive_returnsTheWindow()
    {
        var (controller, _) = NewController();
        var id = Guid.NewGuid();

        var created = await controller.Create(Request(id, DndScope.Lows), CancellationToken.None);
        created.Result.Should().BeOfType<CreatedAtActionResult>();

        var active = OkValue(await controller.GetActive(CancellationToken.None));
        active.Should().ContainSingle().Which.Id.Should().Be(id);
        active[0].Scope.Should().Be(DndScope.Lows);
    }

    [Fact]
    public async Task Create_isIdempotentByClientId()
    {
        var (controller, options) = NewController();
        var id = Guid.NewGuid();

        var first = await controller.Create(Request(id, DndScope.All), CancellationToken.None);
        first.Result.Should().BeOfType<CreatedAtActionResult>();

        // Same id again — returns the stored window (200), does not insert a duplicate.
        var second = await controller.Create(Request(id, DndScope.All), CancellationToken.None);
        second.Result.Should().BeOfType<OkObjectResult>();

        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        (await db.DndWindows.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Create_supersedesTheActiveWindowOfTheSameScope()
    {
        var (controller, options) = NewController();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        await controller.Create(Request(firstId, DndScope.Lows), CancellationToken.None);
        await controller.Create(Request(secondId, DndScope.Lows), CancellationToken.None);

        // Only the newest lows window is active.
        var active = OkValue(await controller.GetActive(CancellationToken.None));
        active.Should().ContainSingle().Which.Id.Should().Be(secondId);

        // The first is cleared with the supersede marker (audit retained).
        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        var superseded = await db.DndWindows.SingleAsync(w => w.Id == firstId);
        superseded.ClearedAt.Should().NotBeNull();
        superseded.ClearedBy.Should().Be(DndWindowsController.SupersededBy);
    }

    [Fact]
    public async Task Create_doesNotSupersedeADifferentScope()
    {
        var (controller, _) = NewController();

        await controller.Create(Request(Guid.NewGuid(), DndScope.Lows), CancellationToken.None);
        await controller.Create(Request(Guid.NewGuid(), DndScope.Highs), CancellationToken.None);

        var active = OkValue(await controller.GetActive(CancellationToken.None));
        active.Select(w => w.Scope).Should().BeEquivalentTo(new[] { DndScope.Lows, DndScope.Highs });
    }

    [Fact]
    public async Task Create_expiredWindow_doesNotSupersedeTheActiveWindow()
    {
        var (controller, options) = NewController();
        var activeId = Guid.NewGuid();
        await controller.Create(Request(activeId, DndScope.Lows), CancellationToken.None);

        // An offline-authored window received after it already expired is recorded for
        // audit but is inactive at receipt — it must not turn off the live mute.
        await controller.Create(
            new CreateDndWindowRequest
            {
                Id = Guid.NewGuid(),
                Scope = DndScope.Lows,
                StartedAt = Now.AddHours(-2),
                EndsAt = Now.AddHours(-1),
            },
            CancellationToken.None);

        var active = OkValue(await controller.GetActive(CancellationToken.None));
        active.Should().ContainSingle().Which.Id.Should().Be(activeId);

        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        (await db.DndWindows.SingleAsync(w => w.Id == activeId)).ClearedAt.Should().BeNull();
    }

    [Fact]
    public async Task Create_futureWindow_doesNotSupersedeTheActiveWindow()
    {
        var (controller, options) = NewController();
        var activeId = Guid.NewGuid();
        await controller.Create(Request(activeId, DndScope.All), CancellationToken.None);

        await controller.Create(
            new CreateDndWindowRequest
            {
                Id = Guid.NewGuid(),
                Scope = DndScope.All,
                StartedAt = Now.AddHours(1),
                EndsAt = Now.AddHours(2),
            },
            CancellationToken.None);

        var active = OkValue(await controller.GetActive(CancellationToken.None));
        active.Should().ContainSingle().Which.Id.Should().Be(activeId);

        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        (await db.DndWindows.SingleAsync(w => w.Id == activeId)).ClearedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetActive_excludesExpiredWindows()
    {
        var (controller, _) = NewController();

        await controller.Create(
            new CreateDndWindowRequest
            {
                Id = Guid.NewGuid(),
                Scope = DndScope.All,
                StartedAt = Now.AddHours(-2),
                EndsAt = Now.AddHours(-1), // already expired
            },
            CancellationToken.None);

        var active = OkValue(await controller.GetActive(CancellationToken.None));
        active.Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_clearsAnActiveWindow_andIsIdempotent()
    {
        var (controller, options) = NewController();
        var id = Guid.NewGuid();
        await controller.Create(Request(id, DndScope.All), CancellationToken.None);

        var cleared = await controller.Clear(id, CancellationToken.None);
        cleared.Result.Should().BeOfType<OkObjectResult>();

        (OkValue(await controller.GetActive(CancellationToken.None))).Should().BeEmpty();

        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        var firstClearedAt = (await db.DndWindows.SingleAsync(w => w.Id == id)).ClearedAt;
        firstClearedAt.Should().NotBeNull();
        // A user-clear is not a supersede.
        (await db.DndWindows.SingleAsync(w => w.Id == id)).ClearedBy.Should().BeNull();

        // Second clear is a no-op — does not rewrite the audit timestamp.
        await controller.Clear(id, CancellationToken.None);
        await using var db2 = new NocturneDbContext(options) { TenantId = Tenant };
        (await db2.DndWindows.SingleAsync(w => w.Id == id)).ClearedAt.Should().Be(firstClearedAt);
    }

    [Fact]
    public async Task Clear_unknownId_returns404()
    {
        var (controller, _) = NewController();
        var result = await controller.Clear(Guid.NewGuid(), CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_idOwnedByAnotherTenant_returns409()
    {
        // Sqlite rather than InMemory: the PK collision must surface as the relational
        // DbUpdateException the controller catches (InMemory throws a raw
        // ArgumentException for duplicate keys).
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        var id = Guid.NewGuid();

        // Seed the id under another tenant. The tenant query filter (and RLS in prod)
        // hides it from the idempotency lookup, so the insert hits the global PK.
        var otherTenant = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        await using (var other = new NocturneDbContext(options) { TenantId = otherTenant })
        {
            await other.Database.EnsureCreatedAsync();
            // dnd_windows.tenant_id is FK'd to tenants; seed both tenant rows.
            other.Tenants.Add(new TenantEntity { Id = Tenant, Slug = "a", DisplayName = "a" });
            other.Tenants.Add(new TenantEntity { Id = otherTenant, Slug = "b", DisplayName = "b" });
            other.DndWindows.Add(new DndWindowEntity
            {
                Id = id,
                TenantId = otherTenant,
                Scope = DndScope.Lows,
                StartedAt = Now.AddMinutes(-1),
                Source = "other-tenant",
            });
            await other.SaveChangesAsync();
        }

        var controller = new DndWindowsController(
            new TestTenantDbContextFactory(new NocturneDbContext(options) { TenantId = Tenant }));

        var result = await controller.Create(Request(id, DndScope.Lows), CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();

        // The other tenant's window is untouched.
        await using var db = new NocturneDbContext(options) { TenantId = otherTenant };
        var stored = await db.DndWindows.SingleAsync(w => w.Id == id);
        stored.TenantId.Should().Be(otherTenant);
        stored.Source.Should().Be("other-tenant");
    }

    [Fact]
    public async Task Create_rejectsEndsBeforeStart()
    {
        var (controller, _) = NewController();
        var result = await controller.Create(
            new CreateDndWindowRequest
            {
                Id = Guid.NewGuid(),
                Scope = DndScope.Lows,
                StartedAt = Now,
                EndsAt = Now.AddMinutes(-5),
            },
            CancellationToken.None);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_rejectsAMissingScope()
    {
        var (controller, _) = NewController();

        // An omitted scope must not fall through to the zero enum member (lows) — a malformed
        // request that silently mutes low alerts is worse than a 400.
        var result = await controller.Create(
            new CreateDndWindowRequest { Id = Guid.NewGuid(), Scope = null, StartedAt = Now },
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_leavesANonOverlappingFutureWindowAlone()
    {
        var (controller, options) = NewController();
        var futureId = Guid.NewGuid();

        // A mute scheduled for later tonight...
        await controller.Create(
            new CreateDndWindowRequest
            {
                Id = futureId,
                Scope = DndScope.All,
                StartedAt = Now.AddHours(6),
                EndsAt = Now.AddHours(14),
            },
            CancellationToken.None);

        // ...must survive a short mute taken out now that ends before it begins. Adding one mute
        // says nothing about a span it never reaches.
        await controller.Create(
            new CreateDndWindowRequest
            {
                Id = Guid.NewGuid(),
                Scope = DndScope.All,
                StartedAt = Now,
                EndsAt = Now.AddMinutes(30),
            },
            CancellationToken.None);

        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        var future = await db.DndWindows.SingleAsync(w => w.Id == futureId);
        future.ClearedAt.Should().BeNull();
        future.ClearedBy.Should().BeNull();
        future.EndsAt.Should().BeCloseTo(Now.AddHours(14), TimeSpan.FromSeconds(1), "not truncated either");
    }

    [Fact]
    public async Task Create_clearsAFutureWindowTheNewSpanWhollyCovers()
    {
        var (controller, options) = NewController();
        var futureId = Guid.NewGuid();

        await controller.Create(
            new CreateDndWindowRequest
            {
                Id = futureId,
                Scope = DndScope.All,
                StartedAt = Now.AddHours(2),
                EndsAt = Now.AddHours(4),
            },
            CancellationToken.None);

        // An open-ended mute from now on covers the scheduled span, so the scheduled one goes —
        // otherwise both would be active from +2h and the resolver would report the earlier
        // one's expiry while the later kept suppressing past it.
        await controller.Create(Request(Guid.NewGuid(), DndScope.All), CancellationToken.None);

        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        var future = await db.DndWindows.SingleAsync(w => w.Id == futureId);
        future.ClearedAt.Should().NotBeNull();
        future.ClearedBy.Should().Be(DndWindowsController.SupersededBy);
    }

    /// <summary>
    /// A running mute is handed over at the boundary rather than switched off: it keeps suppressing
    /// until the new window starts, and only one of the two is ever active at a given instant.
    /// </summary>
    [Fact]
    public async Task Create_truncatesARunningWindowThatTheNewSpanOverlaps()
    {
        var (controller, options) = NewController();
        var runningId = Guid.NewGuid();

        await controller.Create(
            new CreateDndWindowRequest
            {
                Id = runningId,
                Scope = DndScope.All,
                StartedAt = Now.AddHours(-1),
                EndsAt = null,
            },
            CancellationToken.None);

        var laterStart = Now.AddHours(10);
        await controller.Create(
            new CreateDndWindowRequest
            {
                Id = Guid.NewGuid(),
                Scope = DndScope.All,
                StartedAt = laterStart,
                EndsAt = Now.AddHours(18),
            },
            CancellationToken.None);

        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        var running = await db.DndWindows.SingleAsync(w => w.Id == runningId);
        running.ClearedAt.Should().BeNull("the mute is still in force until the new one starts");
        running.EndsAt.Should().BeCloseTo(laterStart, TimeSpan.FromSeconds(1));

        // Still muted right now, and exactly one window is active.
        var active = OkValue(await controller.GetActive(CancellationToken.None));
        active.Should().ContainSingle().Which.Id.Should().Be(runningId);
    }

    [Fact]
    public async Task Create_doesNotStampSupersededOnAnAlreadyExpiredWindow()
    {
        var (controller, options) = NewController();
        var expiredId = Guid.NewGuid();

        await controller.Create(
            new CreateDndWindowRequest
            {
                Id = expiredId,
                Scope = DndScope.Highs,
                StartedAt = Now.AddHours(-3),
                EndsAt = Now.AddHours(-2),
            },
            CancellationToken.None);

        await controller.Create(Request(Guid.NewGuid(), DndScope.Highs), CancellationToken.None);

        // The window ran out on its own; rewriting it as system:superseded would falsify the
        // audit trail these rows are retained for.
        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        var expired = await db.DndWindows.SingleAsync(w => w.Id == expiredId);
        expired.ClearedAt.Should().BeNull();
        expired.ClearedBy.Should().BeNull();
    }

    // ---- helpers ----

    private static CreateDndWindowRequest Request(Guid id, DndScope scope) => new()
    {
        Id = id,
        Scope = scope,
        StartedAt = Now.AddMinutes(-1),
        EndsAt = null,
        Source = "test",
    };

    private static (DndWindowsController Controller, DbContextOptions<NocturneDbContext> Options) NewController()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var seed = new NocturneDbContext(options) { TenantId = Tenant };
        var factory = new TestTenantDbContextFactory(seed);
        return (new DndWindowsController(factory), options);
    }

    private static List<DndWindowResponse> OkValue(ActionResult<List<DndWindowResponse>> result)
    {
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeAssignableTo<List<DndWindowResponse>>().Subject;
    }
}
