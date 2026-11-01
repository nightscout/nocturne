using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Jobs;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.API.Tests.Services.Jobs;

/// <summary>
/// The daily purges run from the job ledger over a migrated PostgreSQL under the runtime role and
/// Row Level Security, in the scope <see cref="JobScheduler"/> opens for each tenant. A "restart" is
/// a fresh scheduler over the same database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DailyJobsPostgresTests(DailyJobsPostgresTests.Database database)
    : IClassFixture<DailyJobsPostgresTests.Database>, IAsyncLifetime
{
    public sealed class Database : IAsyncLifetime
    {
        public TestDatabase Created { get; private set; } = null!;

        public async Task InitializeAsync() =>
            Created = await SharedPostgres.CreateMigratedDatabaseAsync("daily_jobs");

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly List<ServiceProvider> _providers = [];
    private readonly string _app = database.Created.AppConnectionString;
    private readonly string _migrator = database.Created.MigratorConnectionString;

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var reset = new NpgsqlCommand("TRUNCATE job_ledger; TRUNCATE tenants CASCADE;", conn);
        await reset.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();
    }

    public static TheoryData<string> DailyPurges => [SoftDeleteCleanupJob.JobName, AuditRetentionJob.JobName];

    [Theory]
    [MemberData(nameof(DailyPurges))]
    public async Task A_daily_purge_is_not_rerun_by_a_restart_and_runs_again_a_day_later(string job)
    {
        var tenant = await SeedTenantAsync();
        await SeedExpiredAsync(job, tenant);

        var first = Scheduler(job);
        await first.EnrolAsync(default);
        (await RunDueAsync(first)).Should().Be(1);
        (await ExpiredCountAsync(job, tenant)).Should().Be(0);

        await SeedExpiredAsync(job, tenant);
        _clock.Advance(TimeSpan.FromMinutes(10));
        var restarted = Scheduler(job);
        await restarted.EnrolAsync(default);
        (await RunDueAsync(restarted)).Should().Be(0, "a restart does not rerun a purge that ran today");
        (await ExpiredCountAsync(job, tenant)).Should().Be(1);

        _clock.Advance(TimeSpan.FromDays(1));
        (await RunDueAsync(restarted)).Should().Be(1);
        (await ExpiredCountAsync(job, tenant)).Should().Be(0);
    }

    /// <summary>#2504: the cleanup reads each tenant's rows, so it deletes them under FORCE RLS.</summary>
    [Fact]
    public async Task Notification_cleanup_deletes_each_tenants_old_archived_notifications_and_keeps_the_rest()
    {
        var tenants = new[] { await SeedTenantAsync(), await SeedTenantAsync() };
        var now = Start.UtcDateTime;
        foreach (var tenant in tenants)
        {
            await using var db = Context(tenant);
            db.InAppNotifications.AddRange(
                Notification(tenant, "old-archived", archivedAt: now.AddDays(-31)),
                Notification(tenant, "new-archived", archivedAt: now.AddDays(-29)),
                Notification(tenant, "active", archivedAt: null));
            await db.SaveChangesAsync();
        }

        var scheduler = Scheduler(NotificationCleanupJob.JobName);
        await scheduler.EnrolAsync(default);
        (await RunDueAsync(scheduler)).Should().Be(2);

        foreach (var tenant in tenants)
        {
            await using var db = Context(tenant);
            (await db.InAppNotifications.Select(n => n.Title).ToListAsync())
                .Should().BeEquivalentTo(["new-archived", "active"]);
        }
    }

    private static InAppNotificationEntity Notification(Guid tenant, string title, DateTime? archivedAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant,
        UserId = Guid.NewGuid().ToString(),
        Type = "test",
        Category = NotificationCategory.Informational,
        Urgency = NotificationUrgency.Info,
        Title = title,
        CreatedAt = (archivedAt ?? Start.UtcDateTime).AddDays(-1),
        IsArchived = archivedAt is not null,
        ArchivedAt = archivedAt,
    };

    /// <summary>A row the job purges: soft-deleted 40 days ago, or audited 100 days ago.</summary>
    private async Task SeedExpiredAsync(string job, Guid tenant)
    {
        await using var db = Context(tenant);
        if (job == SoftDeleteCleanupJob.JobName)
        {
            db.BodyWeights.Add(new BodyWeightEntity
            {
                Id = Guid.CreateVersion7(), TenantId = tenant, Mills = 0, WeightKg = 70, DeletedAt = DateTime.UtcNow.AddDays(-40),
            });
        }
        else
        {
            db.ReadAccessLog.Add(new ReadAccessLogEntity
            {
                Id = Guid.CreateVersion7(), TenantId = tenant, Endpoint = "/test", StatusCode = 200, CreatedAt = DateTime.UtcNow.AddDays(-100),
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<int> ExpiredCountAsync(string job, Guid tenant)
    {
        await using var db = Context(tenant);
        return job == SoftDeleteCleanupJob.JobName
            ? await db.BodyWeights.IgnoreQueryFilters().CountAsync(b => b.TenantId == tenant)
            : await db.ReadAccessLog.CountAsync();
    }

    private async Task<int> RunDueAsync(JobScheduler scheduler)
    {
        var started = await scheduler.PollAsync(default);
        await scheduler.DrainAsync();
        return started;
    }

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_app)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options)
        {
            TenantId = tenant,
        };

    private JobScheduler Scheduler(string job)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDbContextFactory<NocturneDbContext>(o => o
            .UseNpgsql(_app)
            .AddInterceptors(new TenantConnectionInterceptor()));
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddDataServices();
        services.AddScoped<IInAppNotificationRepository, InAppNotificationRepository>();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddTransient<IJobLedger, JobLedgerRepository>();
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var configuration = provider.GetRequiredService<IConfiguration>();
        IScheduledJob scheduled = job switch
        {
            SoftDeleteCleanupJob.JobName => new SoftDeleteCleanupJob(configuration, NullLogger<SoftDeleteCleanupJob>.Instance),
            AuditRetentionJob.JobName => new AuditRetentionJob(configuration, NullLogger<AuditRetentionJob>.Instance),
            _ => new NotificationCleanupJob(NullLogger<NotificationCleanupJob>.Instance),
        };

        return new JobScheduler(
            [scheduled],
            provider.GetRequiredService<IJobLedger>(),
            provider,
            _clock,
            new JobMetrics(provider.GetRequiredService<IMeterFactory>()),
            Options.Create(new JobSchedulerOptions()),
            NullLogger<JobScheduler>.Instance);
    }

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var insert = new NpgsqlCommand(
            "INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES (@id, @slug, 'T', true, now(), now())", conn);
        insert.Parameters.AddWithValue("id", tenant);
        insert.Parameters.AddWithValue("slug", tenant.ToString("N"));
        await insert.ExecuteNonQueryAsync();
        return tenant;
    }
}
