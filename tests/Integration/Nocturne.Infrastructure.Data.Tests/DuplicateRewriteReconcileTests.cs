using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services.Deduplication;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// A source withdrew its copy of a dose another source also holds, and wrote it again, and the rewrite
/// was linked into a group of its own rather than into the group the withdrawn copy stayed in. The merge
/// joins the two groups as insert-time linking would have, so the dose is counted once, and keeps a
/// distinct dose of that source apart. Against PostgreSQL under the runtime role and Row Level Security.
/// </summary>
[Trait("Category", "Integration")]
public class DuplicateRewriteReconcileTests(DuplicateRewriteReconcileTests.Database database)
    : IClassFixture<DuplicateRewriteReconcileTests.Database>
{
    private const string Pump = "pump-connector";
    private const string Withdrawing = "glooko-connector";

    /// <summary>Inside the tight window of the pump copy.</summary>
    private const int Tight = 10;

    /// <summary>Past the tight window, inside the wide one.</summary>
    private const int Wide = 180;

    /// <summary><see cref="Tight"/> before the pump copy rather than after it.</summary>
    private const int TightBefore = -Tight;

    /// <summary><see cref="Wide"/> before the pump copy rather than after it.</summary>
    private const int WideBefore = -Wide;

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("duplicate_rewrite_reconcile");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    public enum SharedKey { LegacyId, SyncIdentifier, UpstreamFingerprint }

    private readonly DateTime _at = DateTime.UtcNow.AddHours(-1);

    [Theory]
    [InlineData(SharedKey.LegacyId, Tight)]
    [InlineData(SharedKey.SyncIdentifier, Tight)]
    [InlineData(SharedKey.UpstreamFingerprint, Tight)]
    [InlineData(SharedKey.LegacyId, Wide)]
    [InlineData(SharedKey.SyncIdentifier, Wide)]
    [InlineData(SharedKey.UpstreamFingerprint, Wide)]
    [InlineData(SharedKey.LegacyId, TightBefore)]
    [InlineData(SharedKey.SyncIdentifier, TightBefore)]
    [InlineData(SharedKey.UpstreamFingerprint, TightBefore)]
    [InlineData(SharedKey.LegacyId, WideBefore)]
    [InlineData(SharedKey.SyncIdentifier, WideBefore)]
    [InlineData(SharedKey.UpstreamFingerprint, WideBefore)]
    public async Task A_rewrite_in_a_group_of_its_own_joins_the_group_its_withdrawn_copy_stayed_in(
        SharedKey key, int offsetSeconds)
    {
        var tenant = await SeedTenantAsync();
        var shared = Guid.NewGuid().ToString("N");
        var (pump, withdrawn, rewrite) = await SeedSplitRewriteAsync(
            tenant, offsetSeconds,
            withdrawn: b => Share(b, key, shared),
            rewrite: b => Share(b, key, shared));

        var merged = await MergeAsync(tenant);

        merged.Should().Be(1);
        await using var check = Context(tenant);
        var links = await check.LinkedRecords.AsNoTracking().ToListAsync();
        links.Select(l => l.RecordId).Should().BeEquivalentTo([pump, withdrawn, rewrite]);
        links.Select(l => l.CanonicalId).Distinct().Should().ContainSingle();
        (await CountedAsync(check)).Should().Be(1, "the dose is counted once");
    }

    [Theory]
    [InlineData(Tight)]
    [InlineData(Wide)]
    [InlineData(TightBefore)]
    [InlineData(WideBefore)]
    public async Task A_dose_its_source_writes_under_a_new_key_after_withdrawing_its_copy_stays_apart(int offsetSeconds)
    {
        var tenant = await SeedTenantAsync();
        await SeedSplitRewriteAsync(tenant, offsetSeconds, withdrawn: _ => { }, rewrite: _ => { });

        var merged = await MergeAsync(tenant);

        merged.Should().Be(0, "the withdrawn copy holds its source against a record that is not it written again");
        await using var check = Context(tenant);
        (await CountedAsync(check)).Should().Be(2);
    }

    [Theory]
    [InlineData(Tight)]
    [InlineData(Wide)]
    [InlineData(TightBefore)]
    [InlineData(WideBefore)]
    public async Task A_second_live_copy_of_one_source_with_the_same_fingerprint_stays_apart(int offsetSeconds)
    {
        var tenant = await SeedTenantAsync();
        var shared = Guid.NewGuid().ToString("N");
        await SeedSplitRewriteAsync(
            tenant, offsetSeconds,
            withdrawn: b => Share(b, SharedKey.UpstreamFingerprint, shared),
            rewrite: b => Share(b, SharedKey.UpstreamFingerprint, shared),
            withdraw: false);

        var merged = await MergeAsync(tenant);

        merged.Should().Be(0, "only a deleted copy gives up its source to the record written again");
        await using var check = Context(tenant);
        (await CountedAsync(check)).Should().Be(2);
    }

    private static void Share(BolusEntity bolus, SharedKey key, string value)
    {
        switch (key)
        {
            case SharedKey.LegacyId:
                bolus.LegacyId = value;
                break;
            case SharedKey.SyncIdentifier:
                bolus.SyncIdentifier = value;
                break;
            case SharedKey.UpstreamFingerprint:
                bolus.UpstreamFingerprint = value;
                break;
        }
    }

    /// <summary>
    /// A pump copy and the withdrawing source's copy five seconds later linked as one group, the latter
    /// deleted when <paramref name="withdraw"/>, and that source's rewrite <paramref name="offsetSeconds"/>
    /// after the pump copy in a group of its own. Every copy is a 2 U bolus.
    /// </summary>
    /// <remarks>
    /// The merge reads a pair from one side: the tight pass takes the later group's members as the
    /// records joining the earlier one, and the wide pass takes the members of the group whose canonical
    /// id sorts lower. The later group's id is minted lower, so a positive offset reads the live rewrite
    /// as the record on both paths, and a negative one the withdrawn copy.
    /// </remarks>
    private async Task<(Guid Pump, Guid Withdrawn, Guid Rewrite)> SeedSplitRewriteAsync(
        Guid tenant, int offsetSeconds, Action<BolusEntity> withdrawn, Action<BolusEntity> rewrite,
        bool withdraw = true)
    {
        await using var seed = Context(tenant);
        var pump = Bolus(tenant, Pump, _at);
        var copy = Bolus(tenant, Withdrawing, _at.AddSeconds(5));
        withdrawn(copy);
        if (withdraw)
            copy.DeletedAt = DateTime.UtcNow;
        var again = Bolus(tenant, Withdrawing, _at.AddSeconds(offsetSeconds));
        rewrite(again);
        seed.Boluses.AddRange(pump, copy, again);

        var lower = Guid.CreateVersion7(DateTimeOffset.UnixEpoch.AddDays(1));
        var higher = Guid.CreateVersion7(DateTimeOffset.UnixEpoch.AddDays(2));
        var (kept, own) = offsetSeconds > 0 ? (higher, lower) : (lower, higher);
        seed.LinkedRecords.AddRange(
            Link(tenant, kept, pump, primary: true),
            Link(tenant, kept, copy, primary: false),
            Link(tenant, own, again, primary: true));
        await DuplicateGroups.FlagAsync(seed, RecordType.Bolus, copy.Id);
        return (pump.Id, copy.Id, again.Id);
    }

    private static BolusEntity Bolus(Guid tenant, string source, DateTime at) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant,
        Timestamp = at,
        LegacyId = $"{source}_{Guid.NewGuid():N}",
        DataSource = source,
        Insulin = 2.0,
    };

    private static LinkedRecordEntity Link(Guid tenant, Guid canonical, BolusEntity bolus, bool primary) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant,
        CanonicalId = canonical,
        RecordType = RecordTypeKeys.Key(RecordType.Bolus),
        RecordId = bolus.Id,
        SourceTimestamp = new DateTimeOffset(bolus.Timestamp).ToUnixTimeMilliseconds(),
        DataSource = bolus.DataSource!,
        IsPrimary = primary,
    };

    private async Task<int> MergeAsync(Guid tenant)
    {
        await using var context = Context(tenant);
        return await DeduplicationModules.Reconciler(context.AsFactory())
            .MergeAllAsync(RecordType.Bolus, CancellationToken.None);
    }

    /// <summary>The live boluses reads count: those no duplicate flag hides.</summary>
    private static Task<int> CountedAsync(NocturneDbContext context) =>
        context.Boluses.AsNoTracking().CountAsync(b => !b.IsDuplicate);

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant, npgsql: TestContexts.Retrying);

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'duplicate-rewrite-reconcile-test', true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"dedup-rewrite-{tenant:N}"));
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }
}
