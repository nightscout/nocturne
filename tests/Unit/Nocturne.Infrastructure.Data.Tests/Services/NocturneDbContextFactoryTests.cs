using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Services;

/// <summary>
/// What a context from <see cref="INocturneDbContextFactory"/> reads as, per entry point and per
/// provider it was resolved from. The database side of the same matrix is
/// <c>TenantReaderMatrixTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class NocturneDbContextFactoryTests : IAsyncDisposable
{
    private static readonly Guid ScopeTenant = Guid.NewGuid();

    private readonly ServiceProvider _provider;

    public NocturneDbContextFactoryTests()
    {
        var services = new ServiceCollection();
        var database = Guid.NewGuid().ToString();
        services.AddDbContextFactory<NocturneDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddScoped<ITenantAccessor, SettableTenantAccessor>();
        services.AddScoped<IAuditContext, StubAuditContext>();
        services.AddSingleton<FactoryHoldingSingleton>();
        services.AddDataServices();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    [Fact]
    public async Task RootResolvedFactory_ReadsFailClosedDefaults_WhateverAnyScopeHolds()
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, ScopeTenant);
        MarkShare(scope, "glucose.read", clamp: true);

        var root = _provider.GetRequiredService<INocturneDbContextFactory>();
        await using var context = await root.CreateAsync();

        context.TenantId.Should().Be(Guid.Empty);
        context.SubjectId.Should().Be(Guid.Empty);
        context.IsShareContext.Should().BeFalse();
        context.VisibleCategories.Should().BeNull();
        context.ShareFullHistory.Should().BeFalse();
        context.HistoryClamped.Should().BeFalse();
        context.AuditContext.Should().BeNull();
    }

    [Fact]
    public async Task ASingletonFirstResolvedInsideAScope_HoldsARootFactory()
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, ScopeTenant);
        MarkShare(scope, "glucose.read", clamp: true);

        var singleton = scope.ServiceProvider.GetRequiredService<FactoryHoldingSingleton>();
        await using var context = await singleton.Factory.CreateAsync();

        context.TenantId.Should().Be(Guid.Empty,
            "a singleton's dependencies resolve from the root, never from the scope that first asked for it");
        context.IsShareContext.Should().BeFalse();
        context.VisibleCategories.Should().BeNull();
        context.HistoryClamped.Should().BeFalse();
        context.AuditContext.Should().BeNull();
    }

    [Fact]
    public async Task ScopeResolvedFactory_ReadsTheScopeLive_AfterTheContextIsCreated()
    {
        await using var scope = _provider.CreateAsyncScope();
        await using var context = await Factory(scope).CreateAsync();

        context.TenantId.Should().Be(Guid.Empty, "nothing is resolved yet");
        context.IsShareContext.Should().BeFalse();

        Resolve(scope, ScopeTenant);
        MarkShare(scope, "glucose.read", clamp: true);

        context.TenantId.Should().Be(ScopeTenant);
        context.IsShareContext.Should().BeTrue();
        context.VisibleCategories.Should().Be("glucose.read");
        context.HistoryClamped.Should().BeTrue("a share without full history is clamped");
        context.AuditContext.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<IAuditContext>());
    }

    [Fact]
    public async Task CreateForTenantAsync_ReplacesOnlyTheTenant()
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, ScopeTenant);
        MarkShare(scope, "glucose.read", clamp: true);
        var pinned = Guid.NewGuid();

        await using var context = await Factory(scope).CreateForTenantAsync(pinned);

        context.TenantId.Should().Be(pinned);
        context.IsShareContext.Should().BeTrue("a pin can narrow a reader, never widen it");
        context.VisibleCategories.Should().Be("glucose.read");
        context.HistoryClamped.Should().BeTrue();
    }

    [Fact]
    public async Task CreateForSubjectAsync_HasNoTenant_EvenInAResolvedScope()
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, ScopeTenant);
        var subject = Guid.NewGuid();

        await using var context = await Factory(scope).CreateForSubjectAsync(subject);

        context.TenantId.Should().Be(Guid.Empty);
        context.SubjectId.Should().Be(subject);
    }

    [Fact]
    public async Task CreatePlatformAsync_HasNoTenantAndNoSubject_AndKeepsTheReader()
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, ScopeTenant);
        MarkShare(scope, "glucose.read", clamp: false);

        await using var context = await Factory(scope).CreatePlatformAsync();

        context.TenantId.Should().Be(Guid.Empty);
        context.SubjectId.Should().Be(Guid.Empty);
        context.IsShareContext.Should().BeTrue();
    }

    [Fact]
    public async Task CreateSystemPinnedAsync_ReadsAsNoReader_ButKeepsTheScopeAuditActor()
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, ScopeTenant);
        MarkShare(scope, "glucose.read", clamp: true);
        var pinned = Guid.NewGuid();

        await using var context = await Factory(scope).CreateSystemPinnedAsync(pinned);

        context.TenantId.Should().Be(pinned);
        context.IsShareContext.Should().BeFalse();
        context.VisibleCategories.Should().BeNull();
        context.HistoryClamped.Should().BeFalse();
        context.AuditContext.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<IAuditContext>());
    }

    [Fact]
    public async Task APin_OutlastsALaterChangeOfTheScopeTenant()
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, ScopeTenant);
        await using var context = await Factory(scope).CreateAsync();
        var pinned = Guid.NewGuid();

        await context.PinTenantAsync(pinned);
        Resolve(scope, Guid.NewGuid());

        context.TenantId.Should().Be(pinned);
    }

    private static INocturneDbContextFactory Factory(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();

    private static void Resolve(AsyncServiceScope scope, Guid tenantId) =>
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>()
            .SetTenant(new TenantContext(tenantId, "t", "T", true, false));

    private static void MarkShare(AsyncServiceScope scope, string csv, bool clamp)
    {
        var reader = scope.ServiceProvider.GetRequiredService<ICategoryReadContext>();
        reader.MarkShare();
        reader.SetVisibleCategories(csv);
        reader.SetFullHistory(!clamp);
    }

    private sealed class FactoryHoldingSingleton(INocturneDbContextFactory factory)
    {
        public INocturneDbContextFactory Factory { get; } = factory;
    }

    private sealed class SettableTenantAccessor : ITenantAccessor
    {
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;
        public TenantContext? Context { get; private set; }
        public void SetTenant(TenantContext context) => Context = context;
    }

    private sealed class StubAuditContext : IAuditContext
    {
        public Guid? SubjectId => null;
        public string? SubjectName => null;
        public string? AuthType => null;
        public string? IpAddress => null;
        public Guid? TokenId => null;
        public string? TraceId => null;
        public string? Endpoint => null;
    }
}
