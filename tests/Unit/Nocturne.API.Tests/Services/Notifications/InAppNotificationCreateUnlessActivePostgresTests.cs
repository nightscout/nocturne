using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Notifications;

/// <summary>
/// Concurrent creates of one user's notification of one type and source id, on PostgreSQL under
/// the runtime role, leave one active row: the per-reading pass, the sweep and other replicas may
/// raise the same clock notice at once.
/// </summary>
[Trait("Category", "Integration")]
public class InAppNotificationCreateUnlessActivePostgresTests(InAppNotificationCreateUnlessActivePostgresTests.Database database)
    : IClassFixture<InAppNotificationCreateUnlessActivePostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var db = await SharedPostgres.CreateMigratedDatabaseAsync("notification_create_unless_active");
            AppConnectionString = db.AppConnectionString;
            MigratorConnectionString = db.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;

        private DbContextOptions<NocturneDbContext> Options =>
            TestContexts.Options(AppConnectionString, npgsql: TestContexts.Retrying);

        public async Task<(InAppNotificationRepository Repository, NocturneDbContext Reader)> TenantAsync()
        {
            var tenant = Guid.NewGuid();
            await using (var conn = new NpgsqlConnection(MigratorConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                    VALUES (@id, @slug, 'notification-test', true, now(), now())
                    """;
                cmd.Parameters.AddWithValue("id", tenant);
                cmd.Parameters.AddWithValue("slug", $"notification-{tenant:N}");
                await cmd.ExecuteNonQueryAsync();
            }
            return (new InAppNotificationRepository(new NocturneDbContext(Options) { TenantId = tenant }.AsFactory()),
                new NocturneDbContext(Options) { TenantId = tenant });
        }
    }

    private static InAppNotificationEntity Notice() => new()
    {
        UserId = "owner",
        Type = "alerts.deviceClockAhead",
        Category = NotificationCategory.Alert,
        Urgency = NotificationUrgency.Info,
        Title = "device_clock_ahead",
        Source = "alert-clock",
        SourceId = "last_reading_at",
    };

    [Fact]
    public async Task Of_concurrent_creates_one_lands()
    {
        var (repository, reader) = await database.TenantAsync();

        var created = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => repository.CreateUnlessActiveAsync(Notice()))));

        created.Count(c => c is not null).Should().Be(1);
        (await reader.InAppNotifications.CountAsync(n => n.SourceId == "last_reading_at" && !n.IsArchived))
            .Should().Be(1);
    }

    [Fact]
    public async Task Its_metadata_moves_on_the_active_row_without_a_second_one()
    {
        var (repository, reader) = await database.TenantAsync();
        var created = await repository.CreateUnlessActiveAsync(Notice());

        (await repository.ReplaceMetadataBySourceAsync("owner", "alerts.deviceClockAhead", "last_reading_at",
            """{"aheadUntil":"2026-06-10T12:11:00.0000000Z"}""")).Should().BeTrue();

        var rows = await reader.InAppNotifications.Where(n => n.SourceId == "last_reading_at").ToListAsync();
        rows.Should().ContainSingle().Which.Id.Should().Be(created!.Id);
        rows[0].MetadataJson.Should().Contain("2026-06-10T12:11:00");
    }

    [Fact]
    public async Task Once_archived_it_can_be_raised_again()
    {
        var (repository, _) = await database.TenantAsync();
        var first = await repository.CreateUnlessActiveAsync(Notice());
        await repository.ArchiveAsync(first!.Id, NotificationArchiveReason.ConditionMet);

        (await repository.CreateUnlessActiveAsync(Notice())).Should().NotBeNull();
    }
}
