using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.AppBuilds;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Infrastructure.Data.Tests.V4Goldens;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Two first links for one target race to insert under <c>ux_app_build_links_tenant_target</c>. The
/// loser must replace the winner's row rather than fail. It must leave no audit row for the insert it
/// never made.
/// </summary>
/// <remarks>
/// The saves are attributed to a person: a system-attributed save writes no audit row at all, which
/// would hide a stray one.
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
        _provider = TenantReaderData.BuildProvider(fx.AppConnectionString, services =>
        {
            services.AddScoped<TestAuditContext>();
            services.AddScoped<IAuditContext>(sp => sp.GetRequiredService<TestAuditContext>());
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    /// <summary>
    /// Forces the race: a second writer's insert is held open, uncommitted, so the repository reads no
    /// row, blocks on the unique index, and collides once the second writer commits.
    /// </summary>
    [Fact]
    public async Task A_save_that_loses_the_insert_race_replaces_the_row_and_writes_one_update_audit_row()
    {
        var tenant = await TenantReaderData.SeedTenantAsync(fx, _provider);

        await using var rival = await fx.OpenAppConnectionAsync();
        await using var rivalTx = await rival.BeginTransactionAsync();
        await using (var cmd = rival.CreateCommand())
        {
            cmd.Transaction = rivalTx;
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @tid::text, true);
                INSERT INTO app_build_links
                    (id, tenant_id, target_id, installation_id, account_login, account_is_organization,
                     admin_repositories, linked_by_subject_id, linker_github_login, linker_github_id)
                VALUES (gen_random_uuid(), @tid, 'aaps', 1, 'rival', false, ARRAY['rival/my-aaps'],
                        gen_random_uuid(), 'rival', 1)
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, tenant);
        scope.ServiceProvider.GetRequiredService<TestAuditContext>().AttributeToUser(Guid.NewGuid());
        var repository = new AppBuildLinkRepository(scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>());
        var save = repository.SaveAsync(new AppBuildLink("aaps", 2, "alice", false, null,
            ["alice/my-aaps"], Guid.NewGuid(), "alice", 2, default, default), CancellationToken.None);

        await WaitForABlockedInsertAsync();
        save.IsCompleted.Should().BeFalse("the repository's insert waits on the rival's row");
        await rivalTx.CommitAsync();
        await save;

        await using var db = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
        (await db.AppBuildLinks.SingleAsync()).InstallationId.Should().Be(2);
        (await db.MutationAuditLog.Where(row => row.EntityType == "AppBuildLink").Select(row => row.Action).ToListAsync())
            .Should().Equal(["update"], "the failed insert wrote nothing, so it must not be audited");
    }

    /// <remarks>
    /// The race is provoked, not forced. Each round starts its saves together on a tenant with no row,
    /// so their reads find nothing and their inserts collide. A round that happens to serialize still
    /// passes, so the rounds are repeated.
    /// </remarks>
    [Fact]
    public async Task Concurrent_first_links_leave_one_row_one_create_and_one_audit_row_per_write()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var tenant = await TenantReaderData.SeedTenantAsync(fx, _provider);
            using var start = new ManualResetEventSlim();

            var saves = Enumerable.Range(1, Writers).Select(installation => Task.Run(async () =>
            {
                await using var scope = _provider.CreateAsyncScope();
                TenantReaderData.Resolve(scope.ServiceProvider, tenant);
                scope.ServiceProvider.GetRequiredService<TestAuditContext>().AttributeToUser(Guid.NewGuid());
                var repository = new AppBuildLinkRepository(scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>());
                start.Wait();
                await repository.SaveAsync(new AppBuildLink("aaps", installation, "alice", false, null,
                    ["alice/my-aaps"], Guid.NewGuid(), "alice", installation, default, default), CancellationToken.None);
            })).ToList();
            start.Set();

            await FluentActions.Awaiting(() => Task.WhenAll(saves)).Should().NotThrowAsync();

            await using var db = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
            (await db.AppBuildLinks.CountAsync()).Should().Be(1);

            var audit = await db.MutationAuditLog.Where(row => row.EntityType == "AppBuildLink").ToListAsync();
            audit.Should().HaveCount(Writers, "every save wrote the row exactly once, as an insert or a replace");
            audit.Count(row => row.Action == "create").Should().Be(1, "only one insert succeeded");
        }
    }

    private async Task WaitForABlockedInsertAsync()
    {
        await using var conn = await fx.OpenAppConnectionAsync();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT count(*) FROM pg_stat_activity
                WHERE wait_event_type = 'Lock' AND query ILIKE '%app_build_links%'
                """;
            if ((long)(await cmd.ExecuteScalarAsync())! > 0)
                return;
            await Task.Delay(100);
        }

        throw new TimeoutException("the repository's insert never blocked on the rival's uncommitted row");
    }
}
