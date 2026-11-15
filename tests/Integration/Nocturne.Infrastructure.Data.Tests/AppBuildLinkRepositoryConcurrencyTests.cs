using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Models.AppBuilds;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Two first links for one target race to insert under <c>ux_app_build_links_tenant_target</c>; the
/// loser must replace the winner's row rather than fail.
/// </summary>
/// <remarks>
/// The race is provoked, not forced. Each round starts its saves together on a tenant with no row, so
/// their reads find nothing and their inserts collide. A round that happens to serialize still
/// passes, so the rounds are repeated.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class AppBuildLinkRepositoryConcurrencyTests(RlsCompletenessFixture fx) : IAsyncLifetime
{
    private const int Rounds = 5;
    private const int Writers = 8;

    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(fx.AppConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Concurrent_first_links_leave_one_row_and_raise_nothing()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var tenant = await TenantReaderData.SeedTenantAsync(fx, _provider);
            using var start = new ManualResetEventSlim();

            var saves = Enumerable.Range(1, Writers).Select(installation => Task.Run(async () =>
            {
                await using var scope = _provider.CreateAsyncScope();
                TenantReaderData.Resolve(scope.ServiceProvider, tenant);
                var repository = new AppBuildLinkRepository(scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>());
                start.Wait();
                await repository.SaveAsync(new AppBuildLink("aaps", installation, "alice", false, null,
                    ["alice/my-aaps"], Guid.NewGuid(), default, default), CancellationToken.None);
            })).ToList();
            start.Set();

            await FluentActions.Awaiting(() => Task.WhenAll(saves)).Should().NotThrowAsync();

            await using var db = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
            (await db.AppBuildLinks.CountAsync()).Should().Be(1);
        }
    }
}
