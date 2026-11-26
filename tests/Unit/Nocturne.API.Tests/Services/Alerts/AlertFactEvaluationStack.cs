using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>A migrated PostgreSQL database for one test class, reached under the runtime role.</summary>
public sealed class AlertFactEvaluationDatabase : IAsyncLifetime
{
    public TestDatabase Created { get; private set; } = null!;

    public async Task InitializeAsync() =>
        Created = await SharedPostgres.CreateMigratedDatabaseAsync("alert_fact_evaluation");

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <param name="SiteChangeAt">The site change reads show to the pass's scope, as the enricher reads it.</param>
internal sealed record AlertPass(Guid TenantId, IReadOnlyList<AlertRuleSnapshot> Rules, SensorContext Context, DateTime? SiteChangeAt);

/// <summary>
/// The services a committed write's alert evaluation runs through, from the record-changed event to the
/// orchestrator, over PostgreSQL under the runtime role and Row Level Security. The orchestrator records
/// each pass instead of deciding it.
/// </summary>
internal sealed class AlertFactEvaluationStack : IDisposable
{
    public const string SiteAgeOver72Hours = """{"operator":">","value":72}""";

    private readonly AlertFactEvaluationDatabase _database;
    private readonly System.Threading.Channels.Channel<AlertPass> _passes =
        System.Threading.Channels.Channel.CreateUnbounded<AlertPass>();

    public AlertFactEvaluationStack(AlertFactEvaluationDatabase database)
    {
        _database = database;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NocturneDbContext>(o => o
            .UseTenantInterceptor(database.Created.AppConnectionString));
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddDataServices();
        services.AddSingleton(AccountLinkRoutes.Off);
        services.AddSingleton<TimeProvider>(Time);
        services.AddScoped<ISensorGlucoseRepository, SensorGlucoseRepository>();
        services.AddScoped<IAlertRepository, Nocturne.Infrastructure.Data.Repositories.AlertRepository>();
        services.AddScoped<IEvaluatedReadingStore, Nocturne.Infrastructure.Data.Repositories.AlertEvaluatedReadingRepository>();
        services.AddSingleton(Mock.Of<IDemoModeService>());
        services.AddScoped<ICanonicalGlucoseService, CanonicalGlucoseService>();
        services.AddSingleton<AlertEvaluationWatermark>();
        services.AddSingleton<AlertRuleDecisions>();
        services.AddSingleton(Mock.Of<IClockAnomalyNotice>());
        services.AddScoped<ICanonicalAlertEvaluator, CanonicalAlertEvaluator>();
        services.AddScoped<IDeviceEventRepository, DeviceEventRepository>();
        services.AddScoped<IAlertOrchestrator>(sp => new RecordingOrchestrator(
            _passes.Writer, sp.GetRequiredService<ITenantAccessor>(), sp.GetRequiredService<IDeviceEventRepository>()));
        services.AddSingleton<AlertFactEvaluationScheduler>();
        services.AddScoped<IRecordChangeSubscriber, AlertFactsChangedSubscriber>();
        Provider = services.BuildServiceProvider();
    }

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public ServiceProvider Provider { get; }

    public void Dispose() => Provider.Dispose();

    private sealed class RecordingOrchestrator(
        System.Threading.Channels.ChannelWriter<AlertPass> passes, ITenantAccessor tenant,
        IDeviceEventRepository deviceEvents) : IAlertOrchestrator
    {
        public async Task<AlertPassOutcome> EvaluateRulesAsync(
            IReadOnlyList<AlertRuleSnapshot> rules, IReadOnlySet<Guid> enabledRuleIds, SensorContext context,
            long? readAt, CancellationToken ct)
        {
            var siteChange = await deviceEvents.GetLatestOfAnyTypeAsync(
                ConsumableTrackerSpecs.EventTypes(LegacyConsumableKind.Site), null, ct);
            passes.TryWrite(new AlertPass(tenant.TenantId, rules, context, siteChange?.Timestamp));
            return AlertPassOutcome.Decided;
        }
    }

    /// <summary>The next pass of <paramref name="tenant"/>; a write can announce more than once.</summary>
    public async Task<AlertPass> NextPassAsync(Guid tenant)
    {
        while (true)
        {
            var pass = await _passes.Reader.ReadAsync().AsTask().WaitAsync(Eventually.DefaultPatience);
            if (pass.TenantId == tenant)
                return pass;
        }
    }

    /// <summary>Lets the evaluation a write of a fact other than glucose scheduled run.</summary>
    public void PassTheDebounce() => Time.Advance(AlertFactEvaluationScheduler.Debounce);

    public NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(_database.Created.AppConnectionString, tenant);

    public async Task<(TenantContext Tenant, Guid Rule)> SeedTenantAsync(
        string name,
        AlertConditionType condition = AlertConditionType.Threshold,
        string conditionParams = """{"direction":"below","value":70}""")
    {
        var tenant = new TenantContext(Guid.NewGuid(), $"{name}-{Guid.NewGuid():N}", name, true, false);
        await using (var conn = new NpgsqlConnection(_database.Created.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, @name, true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant.TenantId));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", tenant.Slug));
            cmd.Parameters.Add(new NpgsqlParameter("@name", name));
            await cmd.ExecuteNonQueryAsync();
        }

        var rule = Guid.CreateVersion7();
        await using var db = Context(tenant.TenantId);
        db.AlertRules.Add(new AlertRuleEntity
        {
            Id = rule,
            TenantId = tenant.TenantId,
            Name = $"{name} rule",
            ConditionType = condition,
            ConditionParams = conditionParams,
            Severity = AlertRuleSeverity.Critical,
            IsEnabled = true,
            CreatedAt = Time.GetUtcNow().UtcDateTime,
            UpdatedAt = Time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync();
        return (tenant, rule);
    }

    public async Task WriteReadingAsync(TenantContext tenant, double mgdl, TimeSpan ago)
    {
        await using var scope = TenantJobScope.Open(Provider, tenant, "test:cgm");
        await scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>().CreateAsync(
            new SensorGlucose
            {
                Timestamp = Time.GetUtcNow().UtcDateTime - ago,
                Mgdl = mgdl,
                DataSource = "test-cgm",
                Device = "test-cgm",
            },
            WriteOrigin.Live);
    }

    /// <summary>A time PostgreSQL stores and reads back unchanged, <paramref name="ago"/> before the test's now.</summary>
    public DateTime WholeSecondsAgo(TimeSpan ago)
    {
        var at = Time.GetUtcNow().UtcDateTime - ago;
        return at.AddTicks(-(at.Ticks % TimeSpan.TicksPerSecond));
    }

    /// <param name="linked">Whether the site change is its own duplicate group, as insert-time linking leaves it.</param>
    public async Task SeedSiteChangeAsync(
        Guid tenant, DateTime at, string source, string? legacyId = null, bool linked = false)
    {
        await using var db = Context(tenant);
        var record = new DeviceEventEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            EventType = nameof(DeviceEventType.SiteChange),
            Timestamp = at,
            DataSource = source,
            LegacyId = legacyId,
        };
        db.DeviceEvents.Add(record);
        if (linked)
        {
            db.LinkedRecords.Add(new LinkedRecordEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                CanonicalId = Guid.CreateVersion7(),
                RecordType = RecordTypeKeys.Key(RecordType.DeviceEvent),
                RecordId = record.Id,
                SourceTimestamp = at.ToMills(),
                DataSource = source,
                IsPrimary = true,
            });
        }
        await db.SaveChangesAsync();
    }
}
