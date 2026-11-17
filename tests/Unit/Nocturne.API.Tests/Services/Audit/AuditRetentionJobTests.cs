using Nocturne.Tests.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Tests.Services.Jobs;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Services.Audit;

public class AuditRetentionJobTests
{
    private static readonly Dictionary<string, string?> DefaultsDisabled = new()
    {
        [AuditRetentionPolicy.ReadConfigKey] = "0",
        [AuditRetentionPolicy.MutationConfigKey] = "0",
    };

    private readonly string _database = Guid.NewGuid().ToString();

    private NocturneDbContext CreateInMemoryContext() =>
        new(new DbContextOptionsBuilder<NocturneDbContext>().UseInMemoryDatabase(_database).Options);

    /// <summary>
    /// Column names are relational metadata, which the InMemory provider does not build. The
    /// connection string is never opened; only the model is read.
    /// </summary>
    private static NocturneDbContext CreateRelationalModelContext()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        return new NocturneDbContext(options);
    }

    private async Task ConfigureAsync(Guid tenantId, int? readDays, int? mutationDays)
    {
        await using var db = CreateInMemoryContext();
        db.TenantAuditConfig.Add(new TenantAuditConfigEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ReadAuditRetentionDays = readDays,
            MutationAuditRetentionDays = mutationDays,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Runs a <see cref="RecordingAuditRetentionJob"/> for <paramref name="tenantId"/>.</summary>
    private async Task<RecordingAuditRetentionJob> RunAsync(
        Guid tenantId, Dictionary<string, string?>? settings = null, Exception? purgeFails = null)
    {
        var job = new RecordingAuditRetentionJob(
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build(), purgeFails);
        await using var services = new ServiceCollection()
            .AddSingleton(new InMemoryContexts(CreateInMemoryContext).AsNocturneFactory(tenantId))
            .BuildServiceProvider();

        var started = DateTimeOffset.UtcNow;
        var outcome = await job.RunAsync(JobRuns.For(tenantId, services, started), CancellationToken.None);

        outcome.Should().Be(JobOutcome.Resume(null, started + TimeSpan.FromDays(1)));
        return job;
    }

    [Fact]
    public async Task RunAsync_RetentionDisabledEntirely_PurgesNothing()
    {
        var job = await RunAsync(Guid.CreateVersion7(), DefaultsDisabled);

        job.Purges.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ReadAuditRetention_PurgesOnlyTheReadLog()
    {
        var tenantId = Guid.CreateVersion7();
        await ConfigureAsync(tenantId, readDays: 90, mutationDays: null);

        var job = await RunAsync(tenantId, DefaultsDisabled);

        job.Purges.Should().Equal((tenantId, "read_access_log", TimeSpan.FromDays(90)));
    }

    [Fact]
    public async Task RunAsync_MutationAuditRetention_PurgesOnlyTheMutationLog()
    {
        var tenantId = Guid.CreateVersion7();
        await ConfigureAsync(tenantId, readDays: null, mutationDays: 30);

        var job = await RunAsync(tenantId, DefaultsDisabled);

        job.Purges.Should().Equal((tenantId, "mutation_audit_log", TimeSpan.FromDays(30)));
    }

    [Fact]
    public async Task RunAsync_BothRetentionConfigured_PurgesBothTables()
    {
        var tenantId = Guid.CreateVersion7();
        await ConfigureAsync(tenantId, readDays: 90, mutationDays: 30);

        var job = await RunAsync(tenantId, DefaultsDisabled);

        job.Purges.Should().Equal(
            (tenantId, "read_access_log", TimeSpan.FromDays(90)),
            (tenantId, "mutation_audit_log", TimeSpan.FromDays(30)));
    }

    [Fact]
    public async Task RunAsync_APurgeThatFails_FailsTheRunSoTheLedgerRetriesIt()
    {
        var act = () => RunAsync(Guid.CreateVersion7(), purgeFails: new InvalidOperationException("Database connection failed"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RunAsync_UnconfiguredTenant_PurgesAtPlatformDefault()
    {
        var tenantId = Guid.CreateVersion7();

        var job = await RunAsync(tenantId);

        job.Purges.Should().Equal(
            (tenantId, "read_access_log", TimeSpan.FromDays(90)),
            (tenantId, "mutation_audit_log", TimeSpan.FromDays(90)));
    }

    [Fact]
    public async Task RunAsync_TenantConfig_OverridesPlatformDefault()
    {
        var tenantId = Guid.CreateVersion7();
        // A longer window than the platform default must win too, not just a shorter one.
        await ConfigureAsync(tenantId, readDays: null, mutationDays: 365);

        var job = await RunAsync(tenantId);

        job.Purges.Should().Equal(
            (tenantId, "read_access_log", TimeSpan.FromDays(90)),
            (tenantId, "mutation_audit_log", TimeSpan.FromDays(365)));
    }

    [Fact]
    public async Task RunAsync_DefaultDisabled_PurgesOnlyATenantWithItsOwnConfig()
    {
        var configured = Guid.CreateVersion7();
        await ConfigureAsync(configured, readDays: null, mutationDays: 30);

        (await RunAsync(Guid.CreateVersion7(), DefaultsDisabled)).Purges.Should().BeEmpty();
        (await RunAsync(configured, DefaultsDisabled)).Purges.Should().Equal(
            (configured, "mutation_audit_log", TimeSpan.FromDays(30)));
    }

    /// <summary>
    /// The age column is interpolated into raw SQL, so nothing type-checks it. A typo satisfies
    /// the identifier validation, compiles, passes every behavioural test, and then fails every
    /// run at runtime: a purge that deletes nothing, which is the exact failure this job already
    /// shipped once.
    /// </summary>
    [Theory]
    [InlineData(typeof(MutationAuditLogEntity))]
    [InlineData(typeof(ReadAccessLogEntity))]
    public void AgeColumn_ExistsOnEveryTableThePurgeTargets(Type entityType)
    {
        using var context = CreateRelationalModelContext();

        var columns = context.Model.FindEntityType(entityType)!
            .GetProperties()
            .Select(p => p.GetColumnName());

        columns.Should().Contain(AuditRetentionJob.AgeColumn);
    }

    /// <summary>
    /// Same exposure on the sibling job, which shares the purge primitive.
    /// </summary>
    [Fact]
    public void SoftDeleteAgeColumn_ExistsOnEverySoftDeletableTable()
    {
        using var context = CreateRelationalModelContext();

        var softDeletable = context.Model.GetEntityTypes()
            .Where(t => typeof(ISoftDeletable).IsAssignableFrom(t.ClrType)
                        && typeof(ITenantScoped).IsAssignableFrom(t.ClrType))
            .ToList();

        softDeletable.Should().NotBeEmpty("the job must have tables to purge");

        foreach (var entityType in softDeletable)
        {
            entityType.GetProperties().Select(p => p.GetColumnName())
                .Should().Contain(SoftDeleteCleanupJob.AgeColumn,
                    $"{entityType.ClrType.Name} is purged by deleted_at");
        }
    }

    /// <summary>
    /// Records the purges the job would issue instead of running raw SQL, so the effective
    /// retention window per table is observable.
    /// </summary>
    private sealed class RecordingAuditRetentionJob(IConfiguration configuration, Exception? purgeFails)
        : AuditRetentionJob(configuration, NullLogger<AuditRetentionJob>.Instance)
    {
        public List<(Guid TenantId, string Table, TimeSpan MinAge)> Purges { get; } = [];

        internal override Task<int> PurgeBatchedAsync(
            INocturneDbContextFactory contexts, Guid tenantId, string table, TimeSpan minAge, CancellationToken ct)
        {
            if (purgeFails is not null)
                throw purgeFails;
            Purges.Add((tenantId, table, minAge));
            return Task.FromResult(0);
        }
    }

    private sealed class InMemoryContexts(Func<NocturneDbContext> create) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => create();
    }
}
