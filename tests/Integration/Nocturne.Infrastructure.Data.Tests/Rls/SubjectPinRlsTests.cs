using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Configuration;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// What each kind of context reaches of the identity tables under the <c>subject_pin</c> policy,
/// through <see cref="INocturneDbContextFactory"/>, the interceptor and the live policy, as
/// <c>nocturne_app</c>. Every read is raw SQL that never names the pinned subject, so only Row
/// Level Security can narrow it. A pinned context sees the teams its subject is a member of
/// through <c>team_members</c>, which is itself pinned.
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
    private readonly Guid _sharedTeam = Guid.CreateVersion7();
    private readonly Guid _teamOfA = Guid.CreateVersion7();
    private readonly Guid _teamOfB = Guid.CreateVersion7();
    private readonly Guid _staffSubject = Guid.CreateVersion7();
    private readonly Guid _staffTeam = Guid.CreateVersion7();
    private readonly Guid[] _accesses = [Guid.CreateVersion7(), Guid.CreateVersion7()];

    public SubjectPinRlsTests(RlsCompletenessFixture fx) => _fx = fx;

    private Guid[] Subjects => [_subjectA, _subjectB];

    private Guid[] Teams => [_sharedTeam, _teamOfA, _teamOfB];

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);

        // Seeded as the migrator, which never sets the subject GUC: under FORCE ROW LEVEL SECURITY
        // these writes are themselves the proof that an unset GUC admits every row.
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        foreach (var tenant in _tenants.Append(_otherTenant))
        {
            await conn.ExecuteAsync("""
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'subject-pin', true, now(), now())
                """, ("id", tenant), ("slug", $"pin-{tenant:N}"));
        }

        foreach (var subject in Subjects)
        {
            await conn.ExecuteAsync("""
                INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
                VALUES (@id, @name, true, false, now(), now(), 'Approved')
                """, ("id", subject), ("name", $"pin-{subject:N}"));
            await conn.ExecuteAsync("""
                INSERT INTO refresh_tokens (id, token_hash, subject_id, issued_at, expires_at, created_at, updated_at)
                VALUES (@id, @hash, @subject, now(), now() + interval '1 day', now(), now())
                """, ("id", Guid.CreateVersion7()), ("hash", $"{subject:N}"), ("subject", subject));

            foreach (var tenant in _tenants)
            {
                await conn.ExecuteAsync("""
                    INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours)
                    VALUES (@id, @tenant, @subject, '[]'::jsonb, now(), now(), false)
                    """, ("id", Guid.CreateVersion7()), ("tenant", tenant), ("subject", subject));
            }
        }

        foreach (var team in Teams)
        {
            await conn.ExecuteAsync("INSERT INTO teams (id, name) VALUES (@id, 'subject-pin')", ("id", team));
        }

        foreach (var (team, subject) in TeamMemberships)
        {
            await conn.ExecuteAsync("""
                INSERT INTO team_members (id, team_id, subject_id, kind) VALUES (@id, @team, @subject, 'manager')
                """, ("id", Guid.CreateVersion7()), ("team", team), ("subject", subject));
        }

        await SeedStaffTeamAsync(conn);
    }

    private async Task SeedStaffTeamAsync(NpgsqlConnection conn)
    {
        await conn.ExecuteAsync("""
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES (@id, @name, true, false, now(), now(), 'Approved')
            """, ("id", _staffSubject), ("name", $"pin-{_staffSubject:N}"));
        await conn.ExecuteAsync("INSERT INTO teams (id, name) VALUES (@id, 'subject-pin')", ("id", _staffTeam));
        await conn.ExecuteAsync("""
            INSERT INTO team_members (id, team_id, subject_id, kind) VALUES (@id, @team, @subject, 'manager')
            """, ("id", Guid.CreateVersion7()), ("team", _staffTeam), ("subject", _subjectA));
        var staffMember = Guid.CreateVersion7();
        await conn.ExecuteAsync("""
            INSERT INTO team_members (id, team_id, subject_id, kind) VALUES (@id, @team, @subject, 'staff')
            """, ("id", staffMember), ("team", _staffTeam), ("subject", _staffSubject));

        for (var i = 0; i < _tenants.Length; i++)
        {
            await conn.ExecuteAsync("""
                INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours)
                VALUES (@id, @team, @tenant, false)
                """, ("id", _accesses[i]), ("team", _staffTeam), ("tenant", _tenants[i]));
            await conn.ExecuteAsync("""
                INSERT INTO team_site_staff (id, team_tenant_access_id, team_member_id, status)
                VALUES (@id, @access, @member, 'approved')
                """, ("id", Guid.CreateVersion7()), ("access", _accesses[i]), ("member", staffMember));
        }
    }

    private (Guid Team, Guid Subject)[] TeamMemberships =>
        [(_sharedTeam, _subjectA), (_sharedTeam, _subjectB), (_teamOfA, _subjectA), (_teamOfB, _subjectB)];

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
    public async Task SubjectPin_SeesOnlyItsOwnTeamMemberships()
    {
        await using var context = await Factory().CreateForSubjectAsync(_subjectA);

        (await TeamMembershipsAsync(context)).Should().BeEquivalentTo(
            [(_sharedTeam, _subjectA), (_teamOfA, _subjectA)],
            "the query names neither subject, so only the policy can have dropped B's rows, B's row in the shared team among them");
    }

    [Fact]
    public async Task SubjectPin_SeesOnlyTheTeamsItIsAMemberOf()
    {
        await using var context = await Factory().CreateForSubjectAsync(_subjectB);

        (await IdsAsync(context, "teams", "id", Teams)).Should().BeEquivalentTo([_sharedTeam, _teamOfB]);
    }

    [Fact]
    public async Task SubjectPin_CannotAddAnotherSubjectToATeam()
    {
        await using var context = await Factory().CreateForSubjectAsync(_subjectA);

        var insert = () => context.Database.ExecuteSqlAsync($"""
            INSERT INTO team_members (id, team_id, subject_id, kind)
            VALUES ({Guid.CreateVersion7()}, {_teamOfA}, {_subjectB}, 'manager')
            """);

        (await insert.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task SubjectPin_CannotReachATeamItIsNotInToRenameIt()
    {
        await using var context = await Factory().CreateForSubjectAsync(_subjectA);

        var changed = await context.Database.ExecuteSqlAsync($"UPDATE teams SET name = 'taken' WHERE id = {_teamOfB}");

        changed.Should().Be(0);
    }

    [Fact]
    public async Task SubjectPin_OfAStaffMember_ReadsItsTeam()
    {
        await using var context = await Factory().CreateForSubjectAsync(_staffSubject);

        (await IdsAsync(context, "teams", "id", [_staffTeam, _teamOfA, _teamOfB])).Should().Equal(_staffTeam);
    }

    [Fact]
    public async Task SubjectPin_OfAStaffMember_CannotChangeItsTeam()
    {
        await using var context = await Factory().CreateForSubjectAsync(_staffSubject);

        var rename = () => context.Database.ExecuteSqlAsync($"UPDATE teams SET name = 'taken' WHERE id = {_staffTeam}");

        (await rename.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        await using var reader = await Factory().CreatePlatformAsync();
        (await NameOfAsync(reader, _staffTeam)).Should().Be("subject-pin");
    }

    [Fact]
    public async Task SubjectPin_OfAManager_ChangesItsTeam()
    {
        await using var context = await Factory().CreateForSubjectAsync(_subjectA);

        var changed = await context.Database.ExecuteSqlAsync($"UPDATE teams SET name = 'subject-pin' WHERE id = {_staffTeam}");

        changed.Should().Be(1);
    }

    [Fact]
    public async Task TenantPin_SeesOnlyItsTenantsSiteStaffRows()
    {
        await using var context = await Factory().CreateForTenantAsync(_tenants[0]);

        (await IdsAsync(context, "team_site_staff", "team_tenant_access_id", _accesses)).Should().BeEquivalentTo([_accesses[0]],
            "the query names no tenant, so only the policy can have dropped the other site's row");
    }

    [Fact]
    public async Task TenantPin_CannotWriteAnotherTenantsSiteStaffRow()
    {
        await using var context = await Factory().CreateForTenantAsync(_tenants[0]);

        var insert = () => context.Database.ExecuteSqlAsync($"""
            INSERT INTO team_site_staff (id, team_tenant_access_id, team_member_id, status)
            SELECT {Guid.CreateVersion7()}, {_accesses[1]}, m.id, 'pending' FROM team_members m
             WHERE m.team_id = {_staffTeam} AND m.subject_id = {_staffSubject}
            """);

        (await insert.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Unpinned_SeesEverySiteStaffRow()
    {
        await using var context = await Factory().CreatePlatformAsync();

        (await IdsAsync(context, "team_site_staff", "team_tenant_access_id", _accesses)).Should().BeEquivalentTo(_accesses);
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

    /// <summary>
    /// On the context pool, which skips the session reset, the next lessee's subject GUC is the empty
    /// string the interceptor wrote rather than unset, and the policy must read it as unset.
    /// </summary>
    [Fact]
    public async Task PooledConnection_AfterASubjectPin_IsUnrestricted()
    {
        await using var dataSource = PostgresRuntimeOptions.BuildRuntimeDataSource(
            new PostgreSqlConfiguration { ConnectionString = _fx.AppConnectionString, MaxPoolSize = 1 });
        var options = TestContexts.Options(dataSource);

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

        await using (var cmd = next.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = "SELECT current_setting('app.current_subject_id', true)";
            (await cmd.ExecuteScalarAsync()).Should().Be("");
        }

        (await MembershipsAsync(next)).Should().HaveCount(4,
            "the previous lessee's subject pin must not narrow the next one");
    }

    private INocturneDbContextFactory Factory() => _provider.GetRequiredService<INocturneDbContextFactory>();

    private async Task AssertSeesEveryRowAsync(NocturneDbContext context)
    {
        (await MembershipsAsync(context)).Should().HaveCount(4);
        (await SubjectIdsAsync(context, "subjects", "id")).Should().BeEquivalentTo(Subjects);
        (await SubjectIdsAsync(context, "refresh_tokens", "subject_id")).Should().BeEquivalentTo(Subjects);
        (await TeamMembershipsAsync(context)).Should().BeEquivalentTo(TeamMemberships);
        (await IdsAsync(context, "teams", "id", Teams)).Should().BeEquivalentTo(Teams);
    }

    private static async Task<string?> NameOfAsync(NocturneDbContext context, Guid team)
    {
        string? name = null;
        await WithConnectionAsync(context, async connection =>
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM teams WHERE id = @id";
            AddParam(cmd, "@id", team);
            name = (string?)await cmd.ExecuteScalarAsync();
        });
        return name;
    }

    private async Task<List<(Guid Team, Guid Subject)>> TeamMembershipsAsync(NocturneDbContext context)
    {
        var rows = new List<(Guid, Guid)>();
        await WithConnectionAsync(context, async connection =>
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT team_id, subject_id FROM team_members WHERE team_id = ANY(@teams)";
            AddParam(cmd, "@teams", Teams);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add((reader.GetGuid(0), reader.GetGuid(1)));
        });
        return rows;
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

    private Task<List<Guid>> SubjectIdsAsync(NocturneDbContext context, string table, string column) =>
        IdsAsync(context, table, column, Subjects);

    private static async Task<List<Guid>> IdsAsync(NocturneDbContext context, string table, string column, Guid[] among)
    {
        var ids = new List<Guid>();
        await WithConnectionAsync(context, async connection =>
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {column} FROM {table} WHERE {column} = ANY(@ids)";
            AddParam(cmd, "@ids", among);
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

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
