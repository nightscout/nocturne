using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The DND-window reads behind scoped Do Not Disturb (ADR 0004 D5), against real Postgres.
/// </summary>
/// <remarks>
/// These belong here rather than with the unit tests because the behaviour under test *is* the
/// SQL predicate. Every unit test of the enricher and the replay walker mocks
/// <c>IAlertRepository</c>, so nothing there can catch the live read regressing to "every
/// uncleared row" — which is unbounded, since a window that merely expires is never cleared and
/// the read runs once per glucose reading.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class DndWindowRepositoryTests
{
    private readonly RlsCompletenessFixture _fx;

    public DndWindowRepositoryTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task GetUnexpiredDndWindows_excludesExpiredAndClearedWindows()
    {
        var tenant = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await SeedTenantAsync(tenant);

        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var repository = NewRepository(scope.ServiceProvider, tenant);

        // Two rows that must come back...
        await InsertWindowAsync(tenant, DndScope.All, now.AddMinutes(-30), endsAt: null);
        await InsertWindowAsync(tenant, DndScope.Lows, now.AddMinutes(-10), endsAt: now.AddHours(1));
        // ...and three that must not.
        await InsertWindowAsync(tenant, DndScope.Highs, now.AddHours(-4), endsAt: now.AddHours(-3));
        await InsertWindowAsync(tenant, DndScope.Highs, now.AddDays(-30), endsAt: now.AddDays(-30).AddHours(8));
        await InsertWindowAsync(tenant, DndScope.Lows, now.AddMinutes(-20), endsAt: null, clearedAt: now.AddMinutes(-1));

        var windows = await repository.GetUnexpiredDndWindowsAsync(now, CancellationToken.None);

        windows.Should().HaveCount(2, "expired and cleared windows must not reach the evaluation path");
        windows.Select(w => w.Scope).Should().BeEquivalentTo(new[] { DndScope.All, DndScope.Lows });
    }

    /// <summary>
    /// The rows are retained for audit, so the exclusion has to come from the query rather than
    /// from deleting anything — this is what keeps the per-reading read bounded.
    /// </summary>
    [Fact]
    public async Task GetUnexpiredDndWindows_leavesTheExpiredRowsInPlace()
    {
        var tenant = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await SeedTenantAsync(tenant);

        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var repository = NewRepository(scope.ServiceProvider, tenant);

        for (var day = 1; day <= 5; day++)
        {
            await InsertWindowAsync(
                tenant, DndScope.All, now.AddDays(-day), endsAt: now.AddDays(-day).AddHours(1));
        }

        (await repository.GetUnexpiredDndWindowsAsync(now, CancellationToken.None))
            .Should().BeEmpty();

        await using var context = await provider
            .GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(tenant);
        (await context.DndWindows.CountAsync()).Should().Be(5, "cleared/expired rows are audit history");
    }

    /// <summary>
    /// A window is unexpired up to but not including its <c>ends_at</c>, matching
    /// <see cref="DndWindowSnapshot.IsActiveAt"/>'s half-open <c>atUtc &lt; ends</c>.
    /// </summary>
    [Fact]
    public async Task GetUnexpiredDndWindows_treatsEndsAtAsExclusive()
    {
        var tenant = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await SeedTenantAsync(tenant);

        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var repository = NewRepository(scope.ServiceProvider, tenant);

        var endsAt = now.AddMinutes(15);
        await InsertWindowAsync(tenant, DndScope.All, now.AddMinutes(-5), endsAt);

        (await repository.GetUnexpiredDndWindowsAsync(endsAt.AddSeconds(-1), CancellationToken.None))
            .Should().HaveCount(1);
        (await repository.GetUnexpiredDndWindowsAsync(endsAt, CancellationToken.None))
            .Should().BeEmpty("ends_at is exclusive, so the window is over at exactly that instant");
    }

    /// <summary>
    /// Replay needs cleared and expired windows (it resolves <c>cleared_at</c> per tick), so the
    /// receipt-bounded read must not inherit the live read's expiry filter.
    /// </summary>
    [Fact]
    public async Task GetDndWindowsAsOf_stillReturnsClearedAndExpiredWindows()
    {
        var tenant = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await SeedTenantAsync(tenant);

        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var repository = NewRepository(scope.ServiceProvider, tenant);

        await InsertWindowAsync(tenant, DndScope.All, now.AddHours(-4), endsAt: now.AddHours(-3));
        await InsertWindowAsync(tenant, DndScope.Lows, now.AddHours(-2), endsAt: null, clearedAt: now.AddHours(-1));

        var windows = await repository.GetDndWindowsAsOfAsync(now, CancellationToken.None);

        windows.Should().HaveCount(2);
        windows.Should().Contain(w => w.ClearedAt != null);
        windows.Should().Contain(w => w.EndsAt != null && w.ClearedAt == null);
    }

    [Fact]
    public async Task GetUnexpiredDndWindows_isScopedToTheTenant()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await SeedTenantAsync(mine);
        await SeedTenantAsync(theirs);

        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var repository = NewRepository(scope.ServiceProvider, mine);

        await InsertWindowAsync(mine, DndScope.Lows, now.AddMinutes(-5), endsAt: null);
        await InsertWindowAsync(theirs, DndScope.All, now.AddMinutes(-5), endsAt: null);

        var windows = await repository.GetUnexpiredDndWindowsAsync(now, CancellationToken.None);

        windows.Should().ContainSingle().Which.Scope.Should().Be(DndScope.Lows);

        await using var theirScope = provider.CreateAsyncScope();
        (await NewRepository(theirScope.ServiceProvider, theirs).GetUnexpiredDndWindowsAsync(now, CancellationToken.None))
            .Should().ContainSingle().Which.Scope.Should().Be(DndScope.All);
    }

    /// <summary>
    /// Every returned instant must be <see cref="DateTimeKind.Utc"/>: the snapshot's active-at
    /// checks compare naively against a UTC "now", so an Unspecified instant read back from the
    /// column would resolve against the wrong offset.
    /// </summary>
    [Fact]
    public async Task GetUnexpiredDndWindows_normalisesEveryInstantToUtc()
    {
        var tenant = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await SeedTenantAsync(tenant);

        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var repository = NewRepository(scope.ServiceProvider, tenant);

        await InsertWindowAsync(tenant, DndScope.All, now.AddMinutes(-5), endsAt: now.AddHours(2));

        var window = (await repository.GetUnexpiredDndWindowsAsync(now, CancellationToken.None)).Single();

        window.StartedAt.Kind.Should().Be(DateTimeKind.Utc);
        window.EndsAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
        window.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    // ---- helpers ----

    private ServiceProvider BuildProvider() => TenantReaderData.BuildProvider(_fx.AppConnectionString);

    /// <summary>A repository on <paramref name="scope"/>, resolved to <paramref name="tenant"/>.</summary>
    private static AlertRepository NewRepository(IServiceProvider scope, Guid tenant)
    {
        TenantReaderData.Resolve(scope, tenant);
        return new AlertRepository(scope.GetRequiredService<INocturneDbContextFactory>());
    }

    private async Task SeedTenantAsync(Guid tenantId)
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'dnd-window-repo-test', true, now(), now())
            """;
        AddParam(cmd, "@id", tenantId);
        AddParam(cmd, "@slug", $"dnd-{tenantId:N}");
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertWindowAsync(
        Guid tenantId,
        DndScope scope,
        DateTime startedAt,
        DateTime? endsAt,
        DateTime? clearedAt = null)
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        // dnd_windows is FORCE ROW LEVEL SECURITY, so the policy's WITH CHECK applies even to the
        // migrator role — the tenant context has to be set for the INSERT to be admitted at all.
        cmd.CommandText = """
            SELECT set_config('app.current_tenant_id', @tid::text, false);
            INSERT INTO dnd_windows
                (id, tenant_id, scope, started_at, ends_at, cleared_at, source, created_at)
            VALUES
                (gen_random_uuid(), @tid, @scope, @started, @ends, @cleared, 'test', @started)
            """;
        AddParam(cmd, "@tid", tenantId);
        AddParam(cmd, "@scope", ScopeWire(scope));
        AddParam(cmd, "@started", startedAt);
        AddParam(cmd, "@ends", endsAt);
        AddParam(cmd, "@cleared", clearedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The storage form of the scope enum. Spelled out rather than derived so a change to the
    /// converter has to be made deliberately here too — the column is <c>text</c>, and a silent
    /// casing change would make every one of these reads return nothing.
    /// </summary>
    private static string ScopeWire(DndScope scope) => scope switch
    {
        DndScope.Lows => "lows",
        DndScope.Highs => "highs",
        DndScope.All => "all",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static void AddParam(NpgsqlCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
