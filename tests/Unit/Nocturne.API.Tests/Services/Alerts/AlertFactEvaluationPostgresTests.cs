using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
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
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A committed glucose write, through the real repository and record-changed event, is evaluated in
/// a scope of its own pinned to the writer's tenant: on PostgreSQL under the runtime role and Row
/// Level Security, it reads that tenant's rules and readings and no other tenant's.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AlertFactEvaluationPostgresTests(AlertFactEvaluationPostgresTests.Database database)
    : IClassFixture<AlertFactEvaluationPostgresTests.Database>, IDisposable
{
    public sealed class Database : IAsyncLifetime
    {
        public TestDatabase Created { get; private set; } = null!;

        public async Task InitializeAsync() =>
            Created = await SharedPostgres.CreateMigratedDatabaseAsync("alert_fact_evaluation");

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly RecordingOrchestrator _orchestrator = new();
    private ServiceProvider? _provider;

    public void Dispose() => _provider?.Dispose();

    private sealed record Pass(Guid TenantId, IReadOnlyList<AlertRuleSnapshot> Rules, SensorContext Context);

    /// <summary>Every pass, with the tenant its scope read as.</summary>
    private sealed class RecordingOrchestrator
    {
        private readonly System.Threading.Channels.Channel<Pass> _passes =
            System.Threading.Channels.Channel.CreateUnbounded<Pass>();

        public void Add(Pass pass) => _passes.Writer.TryWrite(pass);

        /// <summary>The next pass of <paramref name="tenant"/>; a write can announce more than once.</summary>
        public async Task<Pass> NextAsync(Guid tenant)
        {
            while (true)
            {
                var pass = await _passes.Reader.ReadAsync().AsTask().WaitAsync(Guard);
                if (pass.TenantId == tenant)
                    return pass;
            }
        }
    }

    private sealed class ScopedOrchestrator(RecordingOrchestrator recording, ITenantAccessor tenant) : IAlertOrchestrator
    {
        public Task EvaluateRulesAsync(
            IReadOnlyList<AlertRuleSnapshot> rules, IReadOnlySet<Guid> enabledRuleIds, SensorContext context,
            long? readAt, CancellationToken ct)
        {
            recording.Add(new Pass(tenant.TenantId, rules, context));
            return Task.CompletedTask;
        }
    }

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NocturneDbContext>(o => o
            .UseNpgsql(database.Created.AppConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor()));
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddDataServices();
        services.AddSingleton(AccountLinkRoutes.Off);
        services.AddSingleton<TimeProvider>(_time);
        services.AddScoped<ISensorGlucoseRepository, SensorGlucoseRepository>();
        services.AddScoped<IAlertRepository, Nocturne.Infrastructure.Data.Repositories.AlertRepository>();
        services.AddSingleton(Mock.Of<IDemoModeService>());
        services.AddScoped<ICanonicalGlucoseService, CanonicalGlucoseService>();
        services.AddSingleton<AlertEvaluationWatermark>();
        services.AddSingleton<AlertRuleDecisions>();
        services.AddSingleton(Mock.Of<IClockAnomalyNotice>());
        services.AddScoped<ICanonicalAlertEvaluator, CanonicalAlertEvaluator>();
        services.AddScoped<IAlertOrchestrator>(sp => new ScopedOrchestrator(_orchestrator, sp.GetRequiredService<ITenantAccessor>()));
        services.AddSingleton<AlertFactEvaluationScheduler>();
        services.AddScoped<IRecordChangeSubscriber, AlertFactsChangedSubscriber>();
        return _provider = services.BuildServiceProvider();
    }

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.Created.AppConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = tenant };

    private async Task<(TenantContext Tenant, Guid Rule)> SeedTenantAsync(string name)
    {
        var tenant = new TenantContext(Guid.NewGuid(), $"{name}-{Guid.NewGuid():N}", name, true, false);
        await using (var conn = new NpgsqlConnection(database.Created.MigratorConnectionString))
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
            Name = $"{name} low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = """{"direction":"below","value":70}""",
            Severity = AlertRuleSeverity.Critical,
            IsEnabled = true,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            UpdatedAt = _time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync();
        return (tenant, rule);
    }

    private async Task WriteReadingAsync(IServiceProvider provider, TenantContext tenant, double mgdl, TimeSpan ago)
    {
        await using var scope = TenantJobScope.Open(provider, tenant, "test:cgm");
        await scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>().CreateAsync(
            new SensorGlucose
            {
                Timestamp = _time.GetUtcNow().UtcDateTime - ago,
                Mgdl = mgdl,
                DataSource = "test-cgm",
                Device = "test-cgm",
            },
            WriteOrigin.Live);
    }

    [Fact]
    public async Task AGlucoseWriteIsEvaluatedAgainstItsOwnTenantsRulesAndReadingsOnly()
    {
        var provider = Provider();
        var (a, ruleA) = await SeedTenantAsync("a");
        var (b, ruleB) = await SeedTenantAsync("b");

        await WriteReadingAsync(provider, b, 180, TimeSpan.Zero);
        var passB = await _orchestrator.NextAsync(b.TenantId);
        await WriteReadingAsync(provider, a, 52, TimeSpan.FromMinutes(2));
        var passA = await _orchestrator.NextAsync(a.TenantId);

        passB.Rules.Select(r => r.Id).Should().Equal(ruleB);
        passB.Context.LatestValue.Should().Be(180m);
        passA.Rules.Select(r => r.Id).Should().Equal([ruleA], "tenant b's rule is invisible to tenant a's evaluation");
        passA.Context.LatestValue.Should().Be(52m, "tenant b's newer reading is invisible to tenant a's evaluation");
    }
}
