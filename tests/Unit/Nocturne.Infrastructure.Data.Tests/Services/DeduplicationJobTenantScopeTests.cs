using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Services;

/// <summary>
/// Tests for the tenant scoping of the admin full re-dedup job.
/// <para>
/// The bug these pin is only observable under PostgreSQL FORCE row-level security: a background
/// scope with no tenant pinned sees every tenant-scoped table as empty, so the job completed in
/// milliseconds reporting zero records processed and success. These tests run on SQLite, which has
/// no RLS, so they cannot reproduce the empty reads — they pin the contract that produces them
/// instead: a tenant must be captured before the job starts, and it must be pinned on the
/// background scope before anything is resolved from it.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Deduplication")]
public class DeduplicationJobTenantScopeTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static readonly TenantContext TestTenant =
        new(TestTenantId, "test", "Test", true, false);
    private static readonly TenantContext OtherTenant =
        new(OtherTenantId, "other", "Other", true, false);

    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;

    public DeduplicationJobTenantScopeTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using var seedContext = new NocturneDbContext(_contextOptions) { TenantId = TestTenantId };
        seedContext.Database.EnsureCreated();
        seedContext.Tenants.Add(new TenantEntity { Id = TestTenantId, Slug = "test" });
        seedContext.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task StartDeduplicationJobAsync_ThrowsWhenTheHostHasNoTenantAccessor()
    {
        var service = CreateService(new Mock<IServiceScopeFactory>().Object, tenantAccessor: null);

        var act = async () => await service.StartDeduplicationJobAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*without a resolved tenant*");
    }

    [Fact]
    public async Task StartDeduplicationJobAsync_ThrowsWhenNoTenantIsResolved()
    {
        // An unresolved accessor is what an unauthenticated or misrouted call leaves behind.
        // Starting anyway would produce a job that scans nothing and then reports success.
        var service = CreateService(new Mock<IServiceScopeFactory>().Object, new StubTenantAccessor(null));

        var act = async () => await service.StartDeduplicationJobAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*without a resolved tenant*");
    }

    [Fact]
    public async Task StartDeduplicationJobAsync_PinsTheCapturedTenantBeforeResolvingTheScopedService()
    {
        var calls = new List<string>();
        var scopeAccessor = new RecordingTenantAccessor(calls);
        var jobStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var scopedService = new Mock<IDeduplicationService>();
        scopedService
            .Setup(s => s.DeduplicateAllAsync(It.IsAny<IProgress<DeduplicationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeduplicationResult { Success = true })
            .Callback(() => jobStarted.TrySetResult());

        var scopeFactory = CreateScopeFactory(scopeAccessor, scopedService.Object, calls);
        var service = CreateService(scopeFactory, new StubTenantAccessor(TestTenant));

        await service.StartDeduplicationJobAsync();

        // The job is fire-and-forget, so wait for it rather than racing it.
        await jobStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        scopeAccessor.Context.Should().Be(TestTenant,
            "the background scope runs as the tenant that asked for the job");
        calls.Should().ContainInOrder(
            new[] { "set-tenant", "resolve-dedup-service" },
            "the scoped DbContext reads the accessor when it is built, so the tenant must be pinned first");
    }

    [Fact]
    public async Task GetJobStatusAsync_HidesAnotherTenantsJob()
    {
        var jobId = await StartJobForAsync(TestTenant);

        var owner = CreateService(new Mock<IServiceScopeFactory>().Object, new StubTenantAccessor(TestTenant));
        var stranger = CreateService(new Mock<IServiceScopeFactory>().Object, new StubTenantAccessor(OtherTenant));

        (await owner.GetJobStatusAsync(jobId)).Should().NotBeNull();
        (await stranger.GetJobStatusAsync(jobId)).Should().BeNull(
            "the job dictionaries are static and shared by every tenant in the process");
    }

    [Fact]
    public async Task CancelJobAsync_RefusesAnotherTenantsJob()
    {
        // The job must still be running when cancellation is attempted: a finished job removes its
        // own cancellation source, and then every caller gets false whatever the tenant is.
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var scopedService = new Mock<IDeduplicationService>();
        scopedService
            .Setup(s => s.DeduplicateAllAsync(It.IsAny<IProgress<DeduplicationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                running.TrySetResult();
                await release.Task;
                return new DeduplicationResult { Success = true };
            });

        var calls = new List<string>();
        var scopeFactory = CreateScopeFactory(new RecordingTenantAccessor(calls), scopedService.Object, calls);
        var owner = CreateService(scopeFactory, new StubTenantAccessor(TestTenant));

        var jobId = await owner.StartDeduplicationJobAsync();
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            var stranger = CreateService(new Mock<IServiceScopeFactory>().Object, new StubTenantAccessor(OtherTenant));

            (await stranger.CancelJobAsync(jobId)).Should().BeFalse(
                "another tenant must not be able to cancel a job it did not start");
            (await owner.CancelJobAsync(jobId)).Should().BeTrue(
                "the tenant that started the job can still cancel it");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>
    /// Starts a job whose background scope resolves a mocked service, and returns its id.
    /// </summary>
    private async Task<Guid> StartJobForAsync(TenantContext tenant)
    {
        var calls = new List<string>();
        var scopedService = new Mock<IDeduplicationService>();
        scopedService
            .Setup(s => s.DeduplicateAllAsync(It.IsAny<IProgress<DeduplicationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeduplicationResult { Success = true });

        var scopeFactory = CreateScopeFactory(new RecordingTenantAccessor(calls), scopedService.Object, calls);
        var service = CreateService(scopeFactory, new StubTenantAccessor(tenant));

        return await service.StartDeduplicationJobAsync();
    }

    private DeduplicationService CreateService(IServiceScopeFactory scopeFactory, ITenantAccessor? tenantAccessor)
    {
        var context = new NocturneDbContext(_contextOptions) { TenantId = TestTenantId };
        return new DeduplicationService(
            context, scopeFactory, NullLogger<DeduplicationService>.Instance, tenantAccessor);
    }

    /// <summary>
    /// A scope factory whose scope hands out <paramref name="scopeAccessor"/> and
    /// <paramref name="scopedService"/>, recording the order they are resolved in.
    /// </summary>
    private static IServiceScopeFactory CreateScopeFactory(
        ITenantAccessor scopeAccessor,
        IDeduplicationService scopedService,
        List<string> calls)
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(ITenantAccessor))).Returns(scopeAccessor);
        provider.Setup(p => p.GetService(typeof(IDeduplicationService)))
            .Returns(() =>
            {
                calls.Add("resolve-dedup-service");
                return scopedService;
            });

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);

        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return factory.Object;
    }

    /// <summary>Accessor with a fixed context, standing in for a resolved (or unresolved) request.</summary>
    private sealed class StubTenantAccessor(TenantContext? context) : ITenantAccessor
    {
        public TenantContext? Context { get; } = context;
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;
        public void SetTenant(TenantContext tenantContext) =>
            throw new NotSupportedException("The request-scope accessor is read-only in these tests.");
    }

    /// <summary>Accessor for the background scope that records when the tenant is pinned.</summary>
    private sealed class RecordingTenantAccessor(List<string> calls) : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;

        public void SetTenant(TenantContext tenantContext)
        {
            Context = tenantContext;
            calls.Add("set-tenant");
        }
    }
}
