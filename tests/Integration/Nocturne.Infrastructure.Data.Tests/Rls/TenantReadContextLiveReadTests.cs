using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A context reads its scope's reader when each connection opens, not when it is created: one made
/// before authentication is narrowed once the tenant, share and clamp are resolved. A connection
/// already open keeps the state it opened with, which is what
/// <see cref="RlsPinningExtensions.PinTenantAsync"/> is for.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class TenantReadContextLiveReadTests : IAsyncLifetime
{
    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public TenantReadContextLiveReadTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task AContextCreatedBeforeAuthentication_IsNarrowedOnceTheScopeResolves()
    {
        await using var scope = _provider.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();

        (await TenantReaderData.CountAsync(context, _tenant)).Should().Be(ReaderCounts.Nothing,
            "nothing is resolved yet");

        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        TenantReaderData.MarkShare(
            scope.ServiceProvider,
            ShareDataCategories.ComputeVisibleCategoriesCsv([Scope.StepCountRead]),
            fullHistory: false);

        (await TenantReaderData.CountAsync(context, _tenant)).Should().Be(
            new ReaderCounts(Steps: 1, Foods: 0, Notes: 0, Spans: 0));
    }

    [Fact]
    public async Task AMemberClampAppliedAfterTheContextExists_NarrowsItsNextConnection()
    {
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        await using var context = await scope.ServiceProvider
            .GetRequiredService<INocturneDbContextFactory>().CreateAsync();

        (await TenantReaderData.CountAsync(context, _tenant)).Should().Be(ReaderCounts.Owner);

        scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();

        (await TenantReaderData.CountAsync(context, _tenant)).Should().Be(ReaderCounts.Clamped);
    }

    [Fact]
    public async Task AnOpenConnection_KeepsTheStateItOpenedWith_UntilPinned()
    {
        await using var scope = _provider.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();

        await context.Database.OpenConnectionAsync();
        try
        {
            TenantReaderData.Resolve(scope.ServiceProvider, _tenant);

            context.TenantId.Should().Be(_tenant, "the property reads the scope live");
            (await TenantReaderData.CountAsync(context, _tenant)).Should().Be(ReaderCounts.Nothing,
                "the GUC was written when the connection opened, before the tenant was resolved");

            await context.PinTenantAsync(_tenant);

            (await TenantReaderData.CountAsync(context, _tenant)).Should().Be(ReaderCounts.Owner);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
