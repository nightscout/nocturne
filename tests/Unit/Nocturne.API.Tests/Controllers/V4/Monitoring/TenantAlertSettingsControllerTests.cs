using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// <see cref="TenantAlertSettingsController"/> after ADR 0004 D5: the manual-DND toggle is
/// backed by a <c>scope=all</c> <c>dnd_windows</c> row (request/response shape unchanged).
/// Scheduled fields still persist on the settings row.
/// </summary>
[Trait("Category", "Unit")]
public class TenantAlertSettingsControllerTests
{
    private static readonly Guid Tenant = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task ToggleOn_createsAnActiveAllWindow_andResponseReflectsIt()
    {
        var (controller, options) = NewController();

        var resp = OkValue(await controller.Update(
            new UpdateTenantAlertSettingsRequest { DndManualActive = true }, CancellationToken.None));

        resp.DndManualActive.Should().BeTrue();
        resp.DndManualStartedAt.Should().NotBeNull();

        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        var windows = await db.DndWindows.Where(w => w.Scope == DndScope.All && w.ClearedAt == null).ToListAsync();
        windows.Should().ContainSingle().Which.Source.Should().Be("web");
    }

    [Fact]
    public async Task ToggleOff_clearsTheActiveAllWindow()
    {
        var (controller, options) = NewController();
        await controller.Update(new UpdateTenantAlertSettingsRequest { DndManualActive = true }, CancellationToken.None);

        var resp = OkValue(await controller.Update(
            new UpdateTenantAlertSettingsRequest { DndManualActive = false }, CancellationToken.None));

        resp.DndManualActive.Should().BeFalse();

        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        (await db.DndWindows.CountAsync(w => w.Scope == DndScope.All && w.ClearedAt == null)).Should().Be(0);
        // The row is retained (audit), just cleared.
        (await db.DndWindows.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ToggleOff_clearsAFutureStartedAllWindow()
    {
        var (controller, options) = NewController();

        // A pending mute: uncleared scope=all window that has not started yet.
        var futureId = Guid.CreateVersion7();
        await using (var seed = new NocturneDbContext(options) { TenantId = Tenant })
        {
            seed.DndWindows.Add(new DndWindowEntity
            {
                Id = futureId,
                TenantId = Tenant,
                Scope = DndScope.All,
                StartedAt = DateTime.UtcNow.AddHours(1),
                EndsAt = DateTime.UtcNow.AddHours(2),
                Source = "web",
            });
            await seed.SaveChangesAsync();
        }

        var resp = OkValue(await controller.Update(
            new UpdateTenantAlertSettingsRequest { DndManualActive = false }, CancellationToken.None));
        resp.DndManualActive.Should().BeFalse();

        // An explicit "DND off" cancels the pending mute; it must not activate later.
        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        var window = await db.DndWindows.SingleAsync(w => w.Id == futureId);
        window.ClearedAt.Should().NotBeNull();
        window.ClearedBy.Should().BeNull("toggle-off is a user-clear, not a supersede");
    }

    [Fact]
    public async Task ToggleOn_whenAlreadyOn_keepsOneWindow_andPreservesTheStartedAnchor()
    {
        var (controller, options) = NewController();
        var first = OkValue(await controller.Update(
            new UpdateTenantAlertSettingsRequest { DndManualActive = true }, CancellationToken.None));

        var second = OkValue(await controller.Update(
            new UpdateTenantAlertSettingsRequest { DndManualActive = true, DndManualUntil = DateTime.UtcNow.AddHours(2) },
            CancellationToken.None));

        // Still exactly one active all-window; the for_minutes anchor (StartedAt) is unchanged.
        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        (await db.DndWindows.CountAsync(w => w.Scope == DndScope.All && w.ClearedAt == null)).Should().Be(1);
        second.DndManualStartedAt.Should().Be(first.DndManualStartedAt);
        second.DndManualUntil.Should().NotBeNull();
    }

    [Fact]
    public async Task ToggleOn_withPastUntil_isRejected_andWritesNothing()
    {
        var (controller, options) = NewController();

        var result = await controller.Update(
            new UpdateTenantAlertSettingsRequest
            {
                DndManualActive = true,
                DndManualUntil = DateTime.UtcNow.AddMinutes(-5),
            },
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();

        // No never-active window row, no settings upsert.
        await using var db = new NocturneDbContext(options) { TenantId = Tenant };
        (await db.DndWindows.CountAsync()).Should().Be(0);
        (await db.TenantAlertSettings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Get_reflectsTheActiveWindow_andSchedulePersistsOnTheRow()
    {
        var (controller, _) = NewController();
        await controller.Update(
            new UpdateTenantAlertSettingsRequest
            {
                DndManualActive = true,
                DndScheduleEnabled = true,
                DndScheduleStart = new TimeOnly(22, 0),
                DndScheduleEnd = new TimeOnly(7, 0),
            },
            CancellationToken.None);

        var got = OkValue(await controller.Get(CancellationToken.None));

        got.DndManualActive.Should().BeTrue();
        got.DndScheduleEnabled.Should().BeTrue();
        got.DndScheduleStart.Should().Be(new TimeOnly(22, 0));
        got.DndScheduleEnd.Should().Be(new TimeOnly(7, 0));
    }

    // ---- helpers ----

    private static (TenantAlertSettingsController Controller, DbContextOptions<NocturneDbContext> Options) NewController()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var seed = new NocturneDbContext(options) { TenantId = Tenant };
        var factory = new TestTenantDbContextFactory(seed);
        return (new TenantAlertSettingsController(factory), options);
    }

    private static TenantAlertSettingsResponse OkValue(ActionResult<TenantAlertSettingsResponse> result)
    {
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<TenantAlertSettingsResponse>().Subject;
    }
}
