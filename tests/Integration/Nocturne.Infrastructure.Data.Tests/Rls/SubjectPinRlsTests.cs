using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// What each kind of context reaches of the identity tables under the <c>subject_pin</c> policy,
/// through <see cref="INocturneDbContextFactory"/>, the interceptor and the live policy, as
/// <c>nocturne_app</c>. Every read is raw SQL that never names the pinned subject, so only Row
/// Level Security can narrow it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class SubjectPinRlsTests : IAsyncLifetime
{
    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private readonly Guid _subjectA = Guid.CreateVersion7();
    private readonly Guid _subjectB = Guid.CreateVersion7();
    private readonly Guid[] _tenants = [Guid.NewGuid(), Guid.NewGuid()];
    private readonly Guid _otherTenant = Guid.NewGuid();

    public SubjectPinRlsTests(RlsCompletenessFixture fx) => _fx = fx;

    private Guid[] Subjects => [_subjectA, _subjectB];

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);

        // Seeded as the migrator, which never sets the subject GUC: under FORCE ROW LEVEL SECURITY
        // these writes are themselves the proof that an unset GUC admits every row.
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        foreach (var tenant in _tenants.Append(_otherTenant))
        {
            await ExecuteAsync(conn, """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'subject-pin', true, now(), now())
                """, ("id", tenant), ("slug", $"pin-{tenant:N}"));
        }

        foreach (var subject in Subjects)
        {
            await ExecuteAsync(conn, """
                INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
                VALUES (@id, @name, true, false, now(), now(), 'Approved')
                """, ("id", subject), ("name", $"pin-{subject:N}"));
            await ExecuteAsync(conn, """
                INSERT INTO refresh_tokens (id, token_hash, subject_id, issued_at, expires_at, created_at, updated_at)
                VALUES (@id, @hash, @subject, now(), now() + interval '1 day', now(), now())
                """, ("id", Guid.CreateVersion7()), ("hash", $"{subject:N}"), ("subject", subject));

            foreach (var tenant in _tenants)
            {
                await ExecuteAsync(conn, """
                    INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours)
                    VALUES (@id, @tenant, @subject, '[]'::jsonb, now(), now(), false)
                    """, ("id", Guid.CreateVersion7()), ("tenant", tenant), ("subject", subject));
            }
        }
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task SubjectPin_SeesOnlyItsOwnMemberships()
    {
        await using var context = await Factory().CreateForSubjectAsync(_subjectA);

        var memberships = await MembershipsAsync(context);

        memberships.Should().BeEquivalentTo(_tenants.Select(t => (t, _subjectA)),
            "the query names neither subject, so only the policy can have dropped B's memberships");
    }

    [Fact]
    public async Task SubjectPin_SeesOnlyItsOwnSubjectAndRefreshTokens()
    {
        await using var context = await Factory().CreateForSubjectAsync(_subjectA);

        (await SubjectIdsAsync(context, "subjects", "id")).Should().Equal(_subjectA);
        (await SubjectIdsAsync(context, "refresh_tokens", "subject_id")).Should().Equal(_subjectA);
    }

    [Fact]
    public async Task SubjectPin_ThroughTheEfModel_SeesOnlyItsOwnMemberships()
    {
        await using var context = await Factory().CreateForSubjectAsync(_subjectB);

        var subjects = await context.TenantMembers.AsNoTracking()
            .Where(m => _tenants.Contains(m.TenantId))
            .Select(m => m.SubjectId)
            .ToListAsync();

        subjects.Should().HaveCount(2).And.OnlyContain(s => s == _subjectB);
    }

    [Fact]
    public async Task Platform_SeesEveryRow()
    {
        await using var context = await Factory().CreatePlatformAsync();

        await AssertSeesEveryRowAsync(context);
    }

    [Fact]
    public async Task TenantPin_SeesEveryRow()
    {
        await using var context = await Factory().CreateForTenantAsync(_tenants[0]);

        await AssertSeesEveryRowAsync(context);
    }

    [Fact]
    public async Task ScopeReader_SeesEveryRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenants[0]);
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateAsync();

        await AssertSeesEveryRowAsync(context);
    }

    [Fact]
    public async Task SubjectPin_CannotInsertAnotherSubjectsMembership()
    {
        await using var context = await Factory().CreateForSubjectAsync(_subjectA);

        var insert = () => context.Database.ExecuteSqlAsync($"""
            INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours)
            VALUES ({Guid.CreateVersion7()}, {_otherTenant}, {_subjectB}, '[]'::jsonb, now(), now(), false)
            """);

        (await insert.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task SubjectPin_CannotMoveItsMembershipToAnotherSubject()
    {
        await using var context = await Factory().CreateForSubjectAsync(_subjectA);

        var move = () => context.Database.ExecuteSqlAsync($"""
            UPDATE tenant_members SET subject_id = {_subjectB}
            WHERE tenant_id = {_tenants[0]} AND subject_id = {_subjectA}
            """);

        (await move.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task SubjectPin_CannotReachAnotherSubjectsMembershipToChangeIt()
    {
        await using var context = await Factory().CreateForSubjectAsync(_subjectA);

        var changed = await context.Database.ExecuteSqlAsync(
            $"UPDATE tenant_members SET limit_to_24_hours = true WHERE subject_id = {_subjectB}");

        changed.Should().Be(0);
    }

    [Fact]
    public async Task PooledConnection_AfterASubjectPin_IsUnrestricted()
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

        int pinnedPid;
        await using (var pinned = new NocturneDbContext(options) { SubjectId = _subjectA })
        {
            await pinned.Database.OpenConnectionAsync();
            pinnedPid = await BackendPidAsync(pinned);
            (await MembershipsAsync(pinned)).Should().HaveCount(2);
            await pinned.Database.CloseConnectionAsync();
        }

        await using var next = new NocturneDbContext(options);
        await next.Database.OpenConnectionAsync();
        (await BackendPidAsync(next)).Should().Be(pinnedPid,
            "the pool must hand back the pinned physical connection for this to prove anything");

        (await MembershipsAsync(next)).Should().HaveCount(4,
            "the previous lessee's subject pin must not narrow the next one");
    }

    private INocturneDbContextFactory Factory() => _provider.GetRequiredService<INocturneDbContextFactory>();

    private async Task AssertSeesEveryRowAsync(NocturneDbContext context)
    {
        (await MembershipsAsync(context)).Should().HaveCount(4);
        (await SubjectIdsAsync(context, "subjects", "id")).Should().BeEquivalentTo(Subjects);
        (await SubjectIdsAsync(context, "refresh_tokens", "subject_id")).Should().BeEquivalentTo(Subjects);
    }

    private async Task<List<(Guid Tenant, Guid Subject)>> MembershipsAsync(NocturneDbContext context)
    {
        var rows = new List<(Guid, Guid)>();
        await WithConnectionAsync(context, async connection =>
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT tenant_id, subject_id FROM tenant_members WHERE tenant_id = ANY(@tenants)";
            AddParam(cmd, "@tenants", _tenants);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add((reader.GetGuid(0), reader.GetGuid(1)));
        });
        return rows;
    }

    private async Task<List<Guid>> SubjectIdsAsync(NocturneDbContext context, string table, string column)
    {
        var ids = new List<Guid>();
        await WithConnectionAsync(context, async connection =>
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {column} FROM {table} WHERE {column} = ANY(@subjects)";
            AddParam(cmd, "@subjects", Subjects);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                ids.Add(reader.GetGuid(0));
        });
        return ids;
    }

    private static async Task<int> BackendPidAsync(NocturneDbContext context)
    {
        await using var cmd = context.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT pg_backend_pid()";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task WithConnectionAsync(NocturneDbContext context, Func<DbConnection, Task> read)
    {
        var wasOpen = context.Database.GetDbConnection().State == System.Data.ConnectionState.Open;
        if (!wasOpen)
            await context.Database.OpenConnectionAsync();
        try
        {
            await read(context.Database.GetDbConnection());
        }
        finally
        {
            if (!wasOpen)
                await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
