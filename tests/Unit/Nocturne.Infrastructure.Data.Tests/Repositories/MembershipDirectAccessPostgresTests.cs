using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The request reads carry what a member's direct reason grants beside the summed row, in the same
/// statement, on PostgreSQL under the runtime role: the session read, the API key read and the direct
/// grant read agree; a member with only a team's reason, or whose direct reason has ended, has none;
/// and a row with no reasons is its own direct reason.
/// </summary>
[Trait("Category", "Integration")]
public class MembershipDirectAccessPostgresTests(MembershipDirectAccessPostgresTests.Database database)
    : IClassFixture<MembershipDirectAccessPostgresTests.Database>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Now = new(2026, 11, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DirectEnds = new(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("membership_direct_access");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed record Site(Guid Tenant, SuppliedContextFactory Factory, TenantMembershipRepository Members, Guid ViewerRole, Guid CaretakerRole);

    public enum Read
    {
        Session,
        ApiKey,
        DirectGrant,
    }

    private readonly StatementRecorder _statements = new();

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant, [_statements]);

    private async Task<Site> SiteAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', '{slug}-{tenant:N}', 'direct-access', true, now(), now());
            """);
        var factory = new SuppliedContextFactory(() => Context(tenant));
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        await members.SeedRolesAsync(tenant, Ct);
        return new Site(
            tenant, factory, members,
            (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Viewer, Ct))!.Value,
            (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, Ct))!.Value);
    }

    private static Task<Guid> NewSubjectAsync(Site site, string name) =>
        new SubjectRepository(site.Factory, AccountLinkRoutes.Off).CreateAsync(new NewSubject(name), Ct);

    private Task AddTeamReasonAsync(Site site, Guid member)
    {
        var reason = Guid.CreateVersion7();
        var (teamAccessSql, teamAccess) = TeamReasonSql.NewAccess(site.Tenant, "Clinic");
        return NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            {teamAccessSql}
            INSERT INTO tenant_member_reasons (id, tenant_member_id, kind, source_id, limit_to_24_hours, access_ends_at)
            VALUES ('{reason}', '{member}', 'team', '{teamAccess}', false, NULL);
            INSERT INTO tenant_member_reason_roles (id, tenant_member_reason_id, tenant_role_id)
            VALUES ('{Guid.CreateVersion7()}', '{reason}', '{site.CaretakerRole}');
            """);
    }

    /// <summary>
    /// A viewer let in directly, limited to 24 hours until <see cref="DirectEnds"/>, who also holds the
    /// caretaker role a team's reason gives with neither, the row resynced to the sum.
    /// </summary>
    private async Task<Guid> MixedAsync(Site site, string name)
    {
        var subject = await NewSubjectAsync(site, name);
        (await site.Members.AddMemberAsync(
                new NewTenantMember(site.Tenant, subject, [site.ViewerRole], null, LimitTo24Hours: true, AccessEndsAt: DirectEnds)
                    { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        var member = (await site.Members.FindMemberAsync(site.Tenant, subject, Ct))!.Id;
        await AddTeamReasonAsync(site, member);
        (await site.Members.SetDirectPermissionsAsync(site.Tenant, member, null, Now, Ct)).Should().BeTrue();
        return subject;
    }

    /// <summary>A member whose one reason is the caretaker role a team's access gives.</summary>
    private async Task<Guid> TeamOnlyAsync(Site site, string name)
    {
        var subject = await NewSubjectAsync(site, name);
        var member = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            SELECT set_config('app.current_tenant_id', '{site.Tenant}', false);
            INSERT INTO tenant_members (id, tenant_id, subject_id, limit_to_24_hours, sys_created_at, sys_updated_at)
            VALUES ('{member}', '{site.Tenant}', '{subject}', false, now(), now());
            INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id, sys_created_at)
            VALUES ('{Guid.CreateVersion7()}', '{member}', '{site.CaretakerRole}', now());
            """);
        await AddTeamReasonAsync(site, member);
        return subject;
    }

    private async Task<IReadOnlyList<string>> PermissionsOfAsync(Site site, params Guid[] roles) =>
        MemberPermissions.Effective(
            (await site.Members.ListRolesAsync(site.Tenant, Ct)).Where(r => roles.Contains(r.Id)).Select(r => r.Permissions),
            null).ToList();

    /// <summary>The access and direct access <paramref name="read"/> carries for the subject at <paramref name="now"/>.</summary>
    private static async Task<(TenantMemberAccess? Access, TenantMemberAccess? Direct)> ReadAsync(
        Site site, Guid subject, Read read, DateTime now) =>
        await (await ReaderAsync(site, subject, read))(now);

    /// <summary>Sets up what <paramref name="read"/> needs, and returns the read alone.</summary>
    private static async Task<Func<DateTime, Task<(TenantMemberAccess? Access, TenantMemberAccess? Direct)>>> ReaderAsync(
        Site site, Guid subject, Read read)
    {
        if (read == Read.Session)
        {
            return async now =>
            {
                var membership = (await site.Members.ReadSubjectMembershipAsync(subject, site.Tenant, now, Ct))?.Membership;
                return (membership?.Access, membership?.Direct);
            };
        }

        var tokenHash = $"direct-{Guid.NewGuid():N}";
        var legacySecret = Guid.NewGuid().ToString("N") + "00000000";
        var grants = new OAuthGrantRepository(site.Factory);
        await grants.CreateAsync(
            new NewOAuthGrant(null, subject, OAuthGrantTypes.Direct, [Scope.FullAccess], "direct-access",
                read == Read.DirectGrant ? tokenHash : null, read == Read.ApiKey ? legacySecret : null, null, false), Ct);
        DirectGrantLookup lookup = read == Read.DirectGrant
            ? new DirectGrantLookup.ByTokenHash([tokenHash])
            : new DirectGrantLookup.ByLegacySecretHash(legacySecret);
        return async now =>
        {
            var found = (await grants.FindLiveDirectGrantAsync(site.Tenant, lookup, now, Ct))?.Membership;
            return (found?.Access, found?.Direct);
        };
    }

    [Theory]
    [InlineData(Read.Session)]
    [InlineData(Read.ApiKey)]
    [InlineData(Read.DirectGrant)]
    public async Task Every_request_read_reads_the_direct_reason_in_the_statement_that_reads_the_row(Read read)
    {
        var site = await SiteAsync($"one-statement-{read}");
        var directOnly = await NewSubjectAsync(site, $"one-statement-{read}-direct");
        (await site.Members.AddMemberAsync(
                new NewTenantMember(site.Tenant, directOnly, [site.ViewerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        var mixed = await MixedAsync(site, $"one-statement-{read}-mixed");

        async Task<IReadOnlyList<string>> StatementsOfAsync(Guid subject)
        {
            var reader = await ReaderAsync(site, subject, read);
            _statements.Clear();
            (await reader(Now)).Access.Should().NotBeNull();
            return _statements.All;
        }

        var directStatements = await StatementsOfAsync(directOnly);
        var mixedStatements = await StatementsOfAsync(mixed);

        mixedStatements.Should().HaveSameCount(directStatements, "a team reason adds no round trip");
        mixedStatements.Where(s => s.Contains("tenant_members", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Contain("tenant_member_reasons", "the direct reason is read in the statement that reads the row");
    }

    [Theory]
    [InlineData(Read.Session)]
    [InlineData(Read.ApiKey)]
    [InlineData(Read.DirectGrant)]
    public async Task Every_request_read_carries_the_direct_reason_of_a_mixed_member_beside_the_sum(Read read)
    {
        var site = await SiteAsync($"mixed-{read}");
        var subject = await MixedAsync(site, $"mixed-{read}-member");

        var (access, direct) = await ReadAsync(site, subject, read, Now);

        access!.EffectivePermissions.Should().BeEquivalentTo(await PermissionsOfAsync(site, site.ViewerRole, site.CaretakerRole));
        access.LimitTo24Hours.Should().BeFalse();
        access.AccessEndsAt.Should().BeNull();
        direct!.EffectivePermissions.Should().BeEquivalentTo(await PermissionsOfAsync(site, site.ViewerRole));
        direct.LimitTo24Hours.Should().BeTrue();
        direct.AccessEndsAt.Should().Be(DirectEnds);
    }

    [Theory]
    [InlineData(Read.Session)]
    [InlineData(Read.ApiKey)]
    [InlineData(Read.DirectGrant)]
    public async Task Every_request_read_carries_no_direct_reason_for_a_team_only_member(Read read)
    {
        var site = await SiteAsync($"team-only-{read}");
        var subject = await TeamOnlyAsync(site, $"team-only-{read}-member");

        var (access, direct) = await ReadAsync(site, subject, read, Now);

        access.Should().NotBeNull();
        direct.Should().BeNull();
    }

    [Theory]
    [InlineData(Read.Session)]
    [InlineData(Read.ApiKey)]
    [InlineData(Read.DirectGrant)]
    public async Task A_direct_reason_whose_end_has_passed_counts_as_none(Read read)
    {
        var site = await SiteAsync($"ended-{read}");
        var subject = await MixedAsync(site, $"ended-{read}-member");

        var (access, direct) = await ReadAsync(site, subject, read, DirectEnds.AddDays(1));

        access.Should().NotBeNull("the team's reason still holds the membership");
        direct.Should().BeNull();
    }

    [Theory]
    [InlineData(Read.Session)]
    [InlineData(Read.ApiKey)]
    [InlineData(Read.DirectGrant)]
    public async Task A_row_with_no_reasons_is_its_own_direct_reason(Read read)
    {
        var site = await SiteAsync($"bare-{read}");
        var subject = await NewSubjectAsync(site, $"bare-{read}-member");
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            SELECT set_config('app.current_tenant_id', '{site.Tenant}', false);
            INSERT INTO tenant_members (id, tenant_id, subject_id, limit_to_24_hours, access_ends_at, sys_created_at, sys_updated_at)
            VALUES ('{Guid.CreateVersion7()}', '{site.Tenant}', '{subject}', true, '{DirectEnds:O}', now(), now());
            """);

        var (access, direct) = await ReadAsync(site, subject, read, Now);

        direct.Should().BeSameAs(access);
        access!.LimitTo24Hours.Should().BeTrue();
        access.AccessEndsAt.Should().Be(DirectEnds);
    }

    [Theory]
    [InlineData(Read.Session)]
    [InlineData(Read.ApiKey)]
    [InlineData(Read.DirectGrant)]
    public async Task A_member_whose_one_reason_is_direct_gives_from_the_row(Read read)
    {
        var site = await SiteAsync($"direct-{read}");
        var subject = await NewSubjectAsync(site, $"direct-{read}-member");
        (await site.Members.AddMemberAsync(
                new NewTenantMember(site.Tenant, subject, [site.ViewerRole], null, LimitTo24Hours: true, AccessEndsAt: DirectEnds)
                    { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        (await ReasonKindsAsync(site, subject)).Should().Equal(MembershipReasonKind.Direct);

        var (access, direct) = await ReadAsync(site, subject, read, Now);

        direct.Should().BeSameAs(access);
        access!.EffectivePermissions.Should().BeEquivalentTo(await PermissionsOfAsync(site, site.ViewerRole));
    }

    private async Task<List<string>> ReasonKindsAsync(Site site, Guid subject)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using (var pin = new NpgsqlCommand($"SELECT set_config('app.current_tenant_id', '{site.Tenant}', false);", conn))
            await pin.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT r.kind FROM tenant_member_reasons r JOIN tenant_members m ON m.id = r.tenant_member_id WHERE m.subject_id = @subject;",
            conn);
        cmd.Parameters.AddWithValue("subject", subject);
        var kinds = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            kinds.Add(reader.GetString(0));
        return kinds;
    }
}
