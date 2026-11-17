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
/// <see cref="TenantAlertSettingsRepository"/> over an InMemory store: that the settings write
/// and the manual toggle commit as one save.
/// </summary>
public class TenantAlertSettingsRepositoryTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantAlertSettingsSnapshot Settings =
        new(true, new TimeOnly(22, 0), new TimeOnly(7, 0));

    private readonly string _store = $"tenant_alert_settings_repo_{Guid.NewGuid()}";
    private readonly FakeTimeProvider _clock = new(Now);

    private DbContextOptionsBuilder<NocturneDbContext> Options() => new DbContextOptionsBuilder<NocturneDbContext>()
        .UseInMemoryDatabase(_store)
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

    private NocturneDbContext Db(DbContextOptions<NocturneDbContext>? options = null) =>
        new(options ?? Options().Options) { TenantId = Tenant };

    private DateTime At(double hours) => Now.UtcDateTime.AddHours(hours);

    [Fact]
    public async Task TheSettingsWriteAndTheManualToggleAreStored()
    {
        var repo = new TenantAlertSettingsRepository(new TestNocturneDbContextFactory(Db()), _clock);

        await repo.UpdateAsync(Settings, new ManualDndToggle(true, At(3)), default);

        await using var db = Db();
        var settings = await db.TenantAlertSettings.SingleAsync();
        settings.DndScheduleEnabled.Should().BeTrue();
        settings.DndScheduleStart.Should().Be(new TimeOnly(22, 0));
        settings.UpdatedAt.Should().Be(Now.UtcDateTime);
        (await db.DndWindows.SingleAsync()).EndsAt.Should().Be(At(3));

        (await repo.GetOrCreateAsync(default)).Should().Be(Settings);
    }

    [Fact]
    public async Task AFailedSettingsSaveStoresNoManualWindow()
    {
        var failing = Options().AddInterceptors(new RefuseSettingsSave()).Options;
        var repo = new TenantAlertSettingsRepository(new TestNocturneDbContextFactory(Db(failing)), _clock);

        var update = () => repo.UpdateAsync(Settings, new ManualDndToggle(true, At(3)), default);

        await update.Should().ThrowAsync<InvalidOperationException>();
        await using var db = Db();
        (await db.DndWindows.CountAsync()).Should().Be(0, "the window and the settings row share one save");
        (await db.TenantAlertSettings.CountAsync()).Should().Be(0);
    }

    private sealed class RefuseSettingsSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default) =>
            eventData.Context!.ChangeTracker.Entries<TenantAlertSettingsEntity>().Any()
                ? throw new InvalidOperationException("settings save refused")
                : base.SavingChangesAsync(eventData, result, ct);
    }
}
