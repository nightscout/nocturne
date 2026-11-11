using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Configuration;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The context pool skips Npgsql's session reset on pool return, so a lessee sees only its own RLS
/// session state because <see cref="TenantConnectionInterceptor"/> overwrites all of it on every open,
/// an absent tenant or subject as the empty string. The pool is the one
/// <see cref="ServiceCollectionExtensions.AddPostgreSqlInfrastructure(IServiceCollection, PostgreSqlConfiguration)"/>
/// builds, capped at one physical connection, so every lease below is the same backend.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class PooledLeaseSessionIsolationTests : IAsyncLifetime
{
    private const string ApplicationName = "nocturne-lease-test";

    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public PooledLeaseSessionIsolationTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_fx.AppConnectionString)
        {
            ApplicationName = ApplicationName,
        }.ConnectionString;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgreSqlInfrastructure(connectionString, configuration: null, c =>
        {
            c.MaxPoolSize = 1;
            c.MaxAutoPrepare = 10;
        });
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync()
    {
        await _provider.GetRequiredService<ContextDataSource>().Source.DisposeAsync();
        await _provider.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALease_SeesOnlyItsOwnSessionState_AfterADifferentReader(bool synchronousOpen)
    {
        var otherTenant = Guid.NewGuid();
        int backendPid;

        var shareReader = new CategoryReadContext();
        shareReader.MarkShare();
        shareReader.SetVisibleCategories("stepcount.read,food.read");
        shareReader.SetFullHistory(true);
        shareReader.ClampMemberHistory();

        await using (var share = (await NewContextAsync()).ReadingFrom(reader: shareReader))
        {
            share.TenantId = _tenant;
            share.SubjectId = Guid.NewGuid();
            var connection = await OpenAsync(share, synchronousOpen);
            backendPid = await PidAsync(connection);

            (await TenantReaderData.CountAsync(share, _tenant)).Should().Be(new ReaderCounts(1, 1, 0, 0));

            // Not RLS state: a marker that only Npgsql's reset would clear, so the leases below
            // show that their isolation comes from the interceptor's overwrite and not the reset.
            await ScalarAsync(connection, "SELECT set_config('nocturne_test.marker', 'left', false)");
            await share.Database.CloseConnectionAsync();
        }

        await using (var plain = await NewContextAsync())
        {
            plain.TenantId = otherTenant;
            var connection = await OpenAsync(plain, synchronousOpen);
            (await PidAsync(connection)).Should().Be(backendPid,
                "the pool must hand back the same physical connection for this to prove anything");
            (await SettingAsync(connection, "nocturne_test.marker")).Should().Be("left",
                "the context pool must skip the session reset");

            await AssertSessionAsync(connection, otherTenant.ToString(), HistoryHorizon.SessionTag(otherTenant));
            (await TenantReaderData.CountAsync(plain, _tenant)).Should().Be(ReaderCounts.Nothing,
                "the previous lessee's tenant must not reach a context pinned to another");
            await plain.Database.CloseConnectionAsync();
        }

        await using (var unpinned = await NewContextAsync())
        {
            var connection = await OpenAsync(unpinned, synchronousOpen);
            (await PidAsync(connection)).Should().Be(backendPid);

            await AssertSessionAsync(connection, tenant: "", ApplicationName);
            (await TenantReaderData.CountAsync(unpinned, _tenant)).Should().Be(ReaderCounts.Nothing,
                "an empty tenant must match no tenant's rows, as an unset one does");

            var insert = () => ScalarAsync(connection,
                $"INSERT INTO foods (id, tenant_id, name) VALUES ('{Guid.NewGuid()}', '{_tenant}', 'stray')");
            (await insert.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                    "an empty tenant must refuse a write to any tenant, as an unset one does");
            await unpinned.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// A session write that fails after the physical open must not leave the connection open: the
    /// retry would run on it without the interceptor, still holding the previous lessee's session.
    /// A NUL in the share's category CSV makes the write fail once (22021), which the strategy here
    /// retries as the default one retries a command timeout.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALease_WhoseSessionWriteFails_IsRetriedOnItsOwnSession(bool synchronousOpen)
    {
        var otherTenant = Guid.NewGuid();

        await using (var previous = await NewContextAsync())
        {
            previous.TenantId = _tenant;
            await OpenAsync(previous, synchronousOpen);
            await previous.Database.CloseConnectionAsync();
        }

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_provider.GetRequiredService<ContextDataSource>().Source, npgsql =>
                npgsql.EnableRetryOnFailure(2, TimeSpan.Zero, [PostgresErrorCodes.CharacterNotInRepertoire]))
            .AddInterceptors(_provider.GetRequiredService<TenantConnectionInterceptor>())
            .Options;
        var reader = new ShareReaderFailingOnce();

        await using var retried = new NocturneDbContext(options).ReadingFrom(reader: reader);
        retried.TenantId = otherTenant;
        var tenantSetting = retried.Database.SqlQueryRaw<string>(
            "SELECT current_setting('app.current_tenant_id', true) AS \"Value\"");
        var seen = synchronousOpen ? tenantSetting.ToList() : await tenantSetting.ToListAsync();

        seen.Should().Equal([otherTenant.ToString()],
            "a retry must reopen through the interceptor, never run on the previous lessee's tenant");
        reader.Reads.Should().Be(2, "the first session write must have failed and been retried");
    }

    /// <summary>
    /// The interceptor writes an absent pin as the empty string, which casts to no uuid. Every policy
    /// that reads a pin must therefore take it through <c>NULLIF(..., '')</c>, reading empty as unset.
    /// </summary>
    [Theory]
    [InlineData("app.current_tenant_id")]
    [InlineData("app.current_subject_id")]
    public async Task EveryPolicyReadsAnEmptyPinAsUnset(string guc)
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT tablename || '.' || policyname, coalesce(qual, '') || ' ' || coalesce(with_check, '')
            FROM pg_policies WHERE schemaname = 'public'
            UNION ALL
            SELECT p.proname, p.prosrc
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public'
            """;

        var reads = new List<(string Name, int Reads, int Guarded)>();
        var read = $"current_setting('{guc}'";
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var text = reader.GetString(1);
                var count = Occurrences(text, read);
                if (count > 0)
                    reads.Add((reader.GetString(0), count, Occurrences(text, "NULLIF(" + read)));
            }
        }

        reads.Should().NotBeEmpty();
        reads.Where(r => r.Reads != r.Guarded).Select(r => r.Name).Should().BeEmpty(
            $"an empty {guc} must read as unset, not fail its uuid cast");
    }

    private sealed class ShareReaderFailingOnce : ICategoryReadContext
    {
        public int Reads { get; private set; }
        public bool IsShare => true;
        public string? VisibleCategoriesCsv => ++Reads == 1 ? "food.read\0" : "food.read";
        public bool FullHistory => true;
        public bool IsHistoryClamped => false;
        public void MarkShare() { }
        public void SetVisibleCategories(string csv) { }
        public void SetFullHistory(bool fullHistory) { }
        public void ClampMemberHistory() { }
    }

    private async Task<NocturneDbContext> NewContextAsync() =>
        await _provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync();

    private static async Task<DbConnection> OpenAsync(NocturneDbContext context, bool synchronous)
    {
        if (synchronous)
            context.Database.OpenConnection();
        else
            await context.Database.OpenConnectionAsync();

        return context.Database.GetDbConnection();
    }

    private static async Task AssertSessionAsync(DbConnection connection, string tenant, string applicationName)
    {
        (await SettingAsync(connection, "app.current_tenant_id")).Should().Be(tenant);
        (await SettingAsync(connection, "app.current_subject_id")).Should().BeEmpty(
            "the previous lessee's subject must not reach a context that pins none");
        (await SettingAsync(connection, "application_name")).Should().Be(applicationName);
        (await SettingAsync(connection, "app.is_share")).Should().Be("false");
        (await SettingAsync(connection, "app.visible_categories")).Should().BeEmpty();
        (await SettingAsync(connection, "app.share_full_history")).Should().Be("false");
        (await SettingAsync(connection, "app.history_clamped")).Should().Be("false");
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static async Task<int> PidAsync(DbConnection connection) =>
        Convert.ToInt32(await ScalarAsync(connection, "SELECT pg_backend_pid()"));

    private static async Task<string?> SettingAsync(DbConnection connection, string name) =>
        (string?)await ScalarAsync(connection, $"SELECT current_setting('{name}', true)");

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }
}
