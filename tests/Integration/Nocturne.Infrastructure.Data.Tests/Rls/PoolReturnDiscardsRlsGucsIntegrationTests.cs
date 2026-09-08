using System.Data.Common;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// Proves that Npgsql's DISCARD ALL on pool return is on its own enough to clear every RLS GUC
/// <c>TenantConnectionInterceptor</c> sets, so the interceptor does not have to RESET them on
/// close. The pool is capped at one physical connection, so every lease below is the same
/// backend: a GUC that survived the return would be read back by the next lessee.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class PoolReturnDiscardsRlsGucsIntegrationTests
{
    private static readonly string[] RlsGucs =
    [
        "app.current_tenant_id",
        "app.current_subject_id",
        "app.is_share",
        "app.visible_categories",
        "app.share_full_history",
    ];

    private readonly RlsCompletenessFixture _fx;

    public PoolReturnDiscardsRlsGucsIntegrationTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task PoolReturn_ClearsEveryRlsGuc_WithoutAResetOnClose()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_fx.AppConnectionString)
        {
            MinPoolSize = 0,
            MaxPoolSize = 1,
        }.ConnectionString;

        await using var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(dataSource)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

        var shareTenant = Guid.NewGuid();
        var shareSubject = Guid.NewGuid();
        int backendPid;

        // Lease 1 - a share-flagged, category-restricted context. Every GUC is on the session.
        await using (var share = new NocturneDbContext(options)
        {
            TenantId = shareTenant,
            SubjectId = shareSubject,
            IsShareContext = true,
            VisibleCategories = "glucose.read,treatments.read",
            ShareFullHistory = true,
        })
        {
            var connection = await OpenAsync(share);
            backendPid = Convert.ToInt32(await ScalarAsync(connection, "SELECT pg_backend_pid()"));

            await AssertSettingAsync(connection, "app.current_tenant_id", shareTenant.ToString());
            await AssertSettingAsync(connection, "app.current_subject_id", shareSubject.ToString());
            await AssertSettingAsync(connection, "app.is_share", "true");
            await AssertSettingAsync(connection, "app.visible_categories", "glucose.read,treatments.read");
            await AssertSettingAsync(connection, "app.share_full_history", "true");

            await share.Database.CloseConnectionAsync();
        }

        // Nothing but the pool's DISCARD ALL has run against this backend since. Read the GUCs
        // off a raw lease, so no interceptor gets the chance to overwrite them first.
        await using (var probe = await dataSource.OpenConnectionAsync())
        {
            Convert.ToInt32(await ScalarAsync(probe, "SELECT pg_backend_pid()")).Should().Be(backendPid,
                "the pool must hand back the same physical connection for this to prove anything");

            foreach (var guc in RlsGucs)
            {
                (await SettingAsync(probe, guc)).Should().BeNullOrEmpty(
                    $"{guc} must not survive the pool return");
            }
        }

        // Lease 2 - a plain tenant context. It sets no subject, so a surviving subject id from
        // the share would still be readable here.
        var plainTenant = Guid.NewGuid();
        await using (var plain = new NocturneDbContext(options) { TenantId = plainTenant })
        {
            var connection = await OpenAsync(plain);
            Convert.ToInt32(await ScalarAsync(connection, "SELECT pg_backend_pid()")).Should().Be(backendPid);

            await AssertSettingAsync(connection, "app.current_tenant_id", plainTenant.ToString());
            (await SettingAsync(connection, "app.current_subject_id")).Should().BeNullOrEmpty(
                "the share's subject id must not reach a context that sets none");
            await AssertSettingAsync(connection, "app.is_share", "false");
            (await SettingAsync(connection, "app.visible_categories")).Should().BeEmpty();
            await AssertSettingAsync(connection, "app.share_full_history", "false");

            await plain.Database.CloseConnectionAsync();
        }

        // Lease 3 - an unpinned context. It sets neither tenant nor subject, so both must be
        // clear on the physical connection the previous two lessees pinned.
        await using (var unpinned = new NocturneDbContext(options))
        {
            var connection = await OpenAsync(unpinned);
            Convert.ToInt32(await ScalarAsync(connection, "SELECT pg_backend_pid()")).Should().Be(backendPid);

            (await SettingAsync(connection, "app.current_tenant_id")).Should().BeNullOrEmpty(
                "an unpinned context must match nothing, not the previous lessee's tenant");
            (await SettingAsync(connection, "app.current_subject_id")).Should().BeNullOrEmpty();
            await AssertSettingAsync(connection, "app.is_share", "false");

            await unpinned.Database.CloseConnectionAsync();
        }
    }

    private static async Task<DbConnection> OpenAsync(NocturneDbContext context)
    {
        await context.Database.OpenConnectionAsync();
        return context.Database.GetDbConnection();
    }

    private static async Task AssertSettingAsync(DbConnection connection, string name, string expected)
        => (await SettingAsync(connection, name)).Should().Be(expected);

    private static async Task<string?> SettingAsync(DbConnection connection, string name)
        => (string?)await ScalarAsync(connection, "SELECT current_setting(@name, true)", name);

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql, string? name = null)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        if (name is not null)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = name;
            cmd.Parameters.Add(parameter);
        }

        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }
}
