using System.Data;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// While a scope holds its <see cref="ScopeConnection"/>, the contexts it creates run on that one connection, opened
/// and given its session once; a context whose session would differ opens its own; and the connection is back in the
/// pool when the hold ends, a failed read included. The pool-of-one providers make a second checkout wait for the
/// first, so a read that took its own connection while one was held would time out. A context on the held connection
/// cannot pin another tenant to the session the others share.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class ScopeConnectionTests : IAsyncLifetime
{
    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public ScopeConnectionTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task The_contexts_of_a_held_scope_run_on_one_connection_that_never_reopens()
    {
        await using var provider = PoolOfOne();
        await using var scope = provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        var factory = scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();

        await using var hold = await scope.ServiceProvider.GetRequiredService<ScopeConnection>().HoldConnectionAsync(default);
        var connections = new List<DbConnection>();
        var stateChanges = 0;
        for (var i = 0; i < 3; i++)
        {
            await using var context = await factory.CreateAsync();
            var connection = context.Database.GetDbConnection();
            if (connections.Count == 0)
                connection.StateChange += (_, _) => stateChanges++;
            connections.Add(connection);

            (await context.Notes.CountAsync()).Should().Be(2);
            (await TenantReaderData.CountAsync(context, _tenant)).Should().Be(ReaderCounts.Owner);
        }

        connections.Distinct().Should().ContainSingle();
        stateChanges.Should().Be(0, "the session the hold wrote serves every read; no read opens or closes the connection");
    }

    [Fact]
    public async Task A_context_whose_tenant_differs_from_the_held_session_opens_its_own_and_reads_as_itself()
    {
        var other = Guid.NewGuid();
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        var factory = scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();
        await using var hold = await scope.ServiceProvider.GetRequiredService<ScopeConnection>().HoldConnectionAsync(default);
        await using var held = await factory.CreateAsync();

        TenantReaderData.Resolve(scope.ServiceProvider, other);
        await using var moved = await factory.CreateAsync();
        await using var pinned = await factory.CreateForTenantAsync(_tenant);

        moved.Database.GetDbConnection().Should().NotBeSameAs(held.Database.GetDbConnection());
        (await TenantReaderData.CountAsync(moved, _tenant)).Should().Be(
            ReaderCounts.Nothing, "on the held session it would read the held tenant's rows");
        pinned.Database.GetDbConnection().Should().NotBeSameAs(held.Database.GetDbConnection(),
            "only the scope reader's entry point runs on the held connection");
    }

    [Fact]
    public async Task A_context_whose_clamp_tightened_after_the_hold_opens_its_own_and_reads_clamped()
    {
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        var factory = scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();
        await using var hold = await scope.ServiceProvider.GetRequiredService<ScopeConnection>().HoldConnectionAsync(default);
        await using var held = await factory.CreateAsync();

        scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
        await using var clamped = await factory.CreateAsync();

        clamped.Database.GetDbConnection().Should().NotBeSameAs(held.Database.GetDbConnection());
        (await TenantReaderData.CountAsync(clamped, _tenant)).Should().Be(ReaderCounts.Clamped);
    }

    [Fact]
    public async Task A_context_on_the_held_connection_cannot_pin_another_tenant_to_the_shared_session()
    {
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        var factory = scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();
        await using var hold = await scope.ServiceProvider.GetRequiredService<ScopeConnection>().HoldConnectionAsync(default);
        await using var held = await factory.CreateAsync();

        var pin = () => held.PinTenantAsync(Guid.NewGuid());

        await pin.Should().ThrowAsync<InvalidOperationException>();
        held.TenantId.Should().Be(_tenant);
        await using var next = await factory.CreateAsync();
        next.Database.GetDbConnection().Should().BeSameAs(held.Database.GetDbConnection());
        (await TenantReaderData.CountAsync(next, _tenant)).Should().Be(ReaderCounts.Owner, "the shared session still reads the held tenant");
    }

    [Fact]
    public async Task The_held_connection_is_back_in_the_pool_when_the_hold_ends_after_a_read_throws()
    {
        await using var provider = PoolOfOne();
        await using var scope = provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        var factory = scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();
        DbConnection? held = null;

        var read = async () =>
        {
            await using var hold = await scope.ServiceProvider.GetRequiredService<ScopeConnection>().HoldConnectionAsync(default);
            await using var context = await factory.CreateAsync();
            held = context.Database.GetDbConnection();
            await context.Database.ExecuteSqlRawAsync("SELECT 1 / 0");
        };

        await read.Should().ThrowAsync<PostgresException>();
        held!.State.Should().Be(ConnectionState.Closed);
        await using var after = await factory.CreateAsync();
        after.Database.GetDbConnection().Should().NotBeSameAs(held);
        (await TenantReaderData.CountAsync(after, _tenant)).Should().Be(ReaderCounts.Owner);
    }

    private ServiceProvider PoolOfOne() =>
        TestContexts.BuildProvider(
            _fx.AppConnectionString,
            c => c.MaxPoolSize = 1,
            services => services.AddScoped<ITenantAccessor, SettableTenantAccessor>());
}
