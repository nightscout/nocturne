using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// <see cref="SoftDeleteDedupExtensions.GetHeldOriginalIdsAsync{TEntity}"/> lifts the soft-delete
/// filter to see user tombstones, so the tenant predicate it re-applies and row level security are all
/// that keep another tenant's rows out. Run as <c>nocturne_app</c> against the migrated schema.
/// Activity is stored across these four tables under its id.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class OriginalIdHeldLookupTests(RlsCompletenessFixture fx)
{
    private static readonly string[] LookedUp = ["live", "user-deleted", "swept", "other-tenant", "absent"];

    [Fact]
    public Task StateSpans_HoldLiveRowsAndUserTombstones() =>
        AssertHeldAsync(id => new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), Category = "Exercise", State = "exercise",
            StartTimestamp = DateTime.UtcNow.AddHours(-3), Source = "nightscout-connector", OriginalId = id,
        });

    [Fact]
    public Task HeartRates_HoldLiveRowsAndUserTombstones() =>
        AssertHeldAsync(id => new HeartRateEntity
        {
            Id = Guid.CreateVersion7(), Timestamp = DateTime.UtcNow.AddHours(-3), Bpm = 72, OriginalId = id,
        });

    [Fact]
    public Task StepCounts_HoldLiveRowsAndUserTombstones() =>
        AssertHeldAsync(id => new StepCountEntity
        {
            Id = Guid.CreateVersion7(), Timestamp = DateTime.UtcNow.AddHours(-3), Metric = 800, OriginalId = id,
        });

    [Fact]
    public Task SleepSessions_HoldLiveRowsAndUserTombstones() =>
        AssertHeldAsync(id => new SleepSessionEntity
        {
            Id = Guid.CreateVersion7(), StartTime = DateTime.UtcNow.AddHours(-9), EndTime = DateTime.UtcNow.AddHours(-1),
            Source = "Synthetic", Type = "Overnight", DetectionMethod = "Auto", OriginalId = id,
        });

    [Fact]
    public async Task SleepSessions_OfAnotherSource_DoNotHoldTheId()
    {
        var tenant = await CreateTenantAsync();
        SleepSessionEntity Session(string source, string id) => new()
        {
            Id = Guid.CreateVersion7(), TenantId = tenant,
            StartTime = DateTime.UtcNow.AddHours(-9), EndTime = DateTime.UtcNow.AddHours(-1),
            Source = source, Type = "Overnight", DetectionMethod = "Auto", OriginalId = id,
        };

        await using (var ctx = await OpenAsAppAsync(tenant))
        {
            ctx.Add(Session("Manual", "same-source"));
            ctx.Add(Session("Apple", "other-source"));
            await ctx.SaveChangesAsync();
        }

        await using var lookup = await OpenAsAppAsync(tenant);
        var held = await lookup.GetHeldOriginalIdsAsync<SleepSessionEntity>(
            ["same-source", "other-source"], s => s.Source == "Manual");

        held.Should().BeEquivalentTo(["same-source"],
            "a session is keyed by source and original id, so another source's session is a different record");
    }

    private async Task AssertHeldAsync<TEntity>(Func<string, TEntity> row)
        where TEntity : class, ITenantScoped, ISoftDeletable, IOriginalIdentified
    {
        var tenant = await CreateTenantAsync();
        var other = await CreateTenantAsync();

        await using (var ctx = await OpenAsAppAsync(tenant))
        {
            ctx.Add(Stamp(row("live"), tenant, deletedAt: null));
            var userDeleted = Stamp(row("user-deleted"), tenant, DateTime.UtcNow);
            ctx.Add(userDeleted);
            ctx.Entry(userDeleted).Property("DeletedByUser").CurrentValue = true;
            ctx.Add(Stamp(row("swept"), tenant, DateTime.UtcNow));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = await OpenAsAppAsync(other))
        {
            ctx.Add(Stamp(row("other-tenant"), other, deletedAt: null));
            await ctx.SaveChangesAsync();
        }

        await using var lookup = await OpenAsAppAsync(tenant);
        var held = await lookup.GetHeldOriginalIdsAsync<TEntity>(LookedUp);

        held.Should().BeEquivalentTo(["live", "user-deleted"],
            "a live row and a user tombstone hold the id; a system sweep, another tenant's row and an unknown id do not");
    }

    private static TEntity Stamp<TEntity>(TEntity entity, Guid tenant, DateTime? deletedAt)
        where TEntity : ITenantScoped, ISoftDeletable
    {
        entity.TenantId = tenant;
        entity.DeletedAt = deletedAt;
        return entity;
    }

    private async Task<Guid> CreateTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@tid, @slug, 'held-original-id-test', true, now(), now())
            """;
        cmd.Parameters.AddWithValue("tid", tenant);
        cmd.Parameters.AddWithValue("slug", $"held-{tenant:N}");
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }

    private async Task<NocturneDbContext> OpenAsAppAsync(Guid tenant)
    {
        var conn = await fx.OpenAppConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false), set_config('app.is_share', 'false', false)";
            cmd.Parameters.AddWithValue("tid", tenant.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        return new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn, contextOwnsConnection: true)
            .Options) { TenantId = tenant };
    }
}
