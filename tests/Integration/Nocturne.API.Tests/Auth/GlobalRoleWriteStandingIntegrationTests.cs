using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// The V2 roles API writes the global roles every tenant's subjects hold, so a write needs
/// instance-wide standing. Full access on one tenant is not enough.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public partial class GlobalRoleWriteStandingIntegrationTests : ApiIntegrationTestBase
{
    private const string Roles = "/api/v2/authorization/roles";
    private const string Readable = "readable";

    public GlobalRoleWriteStandingIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public enum Write
    {
        Create,
        Rename,
        Repermission,
        Delete,
    }

    public enum Standing
    {
        PlatformAdminSession,
        InstanceKey,
        SoleTenantOwnerSession,
        SoleTenantApiSecret,
        SoleTenantOwnerBesideADemoTenant,
    }

    /// <summary>
    /// A caller with full access on tenant A who has no standing over an instance that has another
    /// tenant: an owner of A beside an active tenant, an owner of A beside a deactivated tenant (it
    /// still counts), and a platform admin presenting a credential that does not carry the flag.
    /// </summary>
    public enum Refused
    {
        OwnerBesideAnActiveTenant,
        OwnerBesideADeactivatedTenant,
        PlatformAdminDirectGrant,
    }

    [Theory]
    [InlineData(Refused.OwnerBesideAnActiveTenant, Write.Create)]
    [InlineData(Refused.OwnerBesideAnActiveTenant, Write.Rename)]
    [InlineData(Refused.OwnerBesideAnActiveTenant, Write.Repermission)]
    [InlineData(Refused.OwnerBesideAnActiveTenant, Write.Delete)]
    [InlineData(Refused.OwnerBesideADeactivatedTenant, Write.Create)]
    [InlineData(Refused.OwnerBesideADeactivatedTenant, Write.Rename)]
    [InlineData(Refused.OwnerBesideADeactivatedTenant, Write.Repermission)]
    [InlineData(Refused.OwnerBesideADeactivatedTenant, Write.Delete)]
    [InlineData(Refused.PlatformAdminDirectGrant, Write.Create)]
    [InlineData(Refused.PlatformAdminDirectGrant, Write.Rename)]
    [InlineData(Refused.PlatformAdminDirectGrant, Write.Repermission)]
    [InlineData(Refused.PlatformAdminDirectGrant, Write.Delete)]
    public async Task A_caller_without_standing_over_the_instance_is_refused_a_global_role_write_as_a_member_without_full_access_is(
        Refused caller, Write write)
    {
        await using var conn = await OpenPostgresAsync();
        var tenantA = await AuthTestHelpers.GetTenantIdAsync(conn);
        var slugA = await SlugAsync(conn, tenantA);
        var slugB = $"roles-b-{Guid.NewGuid():N}"[..20];
        var tenantB = await AuthTestHelpers.SeedTenantAsync(Fixture, slugB, "Roles B");
        var baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);
        var target = new Target(Guid.Empty, Readable, Readable, Seeded: false);
        var readable = await PermissionsOfAsync(conn, Readable);
        Guid? holderId = null;
        try
        {
            if (caller == Refused.OwnerBesideADeactivatedTenant)
                await conn.ExecuteAsync("UPDATE tenants SET is_active = false WHERE id = @id;", ("id", tenantB));

            var (memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(
                conn, tenantA, "Roles A Reader", [Scope.GlucoseRead]);
            (holderId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantB, "Roles B Holder");

            target = await SeedTargetAsync(conn, write);
            if (write != Write.Create)
                await conn.ExecuteAsync("INSERT INTO subject_roles (subject_id, role_id, assigned_at) VALUES (@s, @r, now());",
                    ("s", holderId.Value), ("r", target.Id));

            using var refusedCaller = caller == Refused.PlatformAdminDirectGrant
                ? await PlatformAdminDirectGrantAsync(conn, tenantA, slugA, baseDomain)
                : AuthTestHelpers.CreateTenantBearerClient(Fixture, slugA, baseDomain,
                    await AuthTestHelpers.IssueSessionTokenAsync(Fixture, await OwnerAsync(conn, tenantA, "Roles A Owner")));
            using var member = AuthTestHelpers.CreateTenantBearerClient(
                Fixture, slugA, baseDomain, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, memberId));

            (await refusedCaller.GetAsync(Roles)).StatusCode.Should().Be(HttpStatusCode.OK,
                "the caller holds full access on its tenant, so only the standing check refuses the write");

            var refused = await ShapeOfAsync(await SendAsync(member, write, target));
            refused.Should().StartWith("403");

            var rolesBefore = await RolesSnapshotAsync(conn);
            var holderBefore = await SessionAuthorityAsync(holderId.Value);

            (await ShapeOfAsync(await SendAsync(refusedCaller, write, target))).Should().Be(refused);

            (await RolesSnapshotAsync(conn)).Should().Be(rolesBefore);
            (await SessionAuthorityAsync(holderId.Value)).Should().Be(holderBefore);
        }
        finally
        {
            await RestoreAsync(conn, target, readable);
            await RemoveTenantAsync(conn, tenantB, holderId);
        }
    }

    [Theory]
    [InlineData(Standing.PlatformAdminSession, Write.Create)]
    [InlineData(Standing.PlatformAdminSession, Write.Rename)]
    [InlineData(Standing.PlatformAdminSession, Write.Repermission)]
    [InlineData(Standing.PlatformAdminSession, Write.Delete)]
    [InlineData(Standing.InstanceKey, Write.Create)]
    [InlineData(Standing.InstanceKey, Write.Rename)]
    [InlineData(Standing.InstanceKey, Write.Repermission)]
    [InlineData(Standing.InstanceKey, Write.Delete)]
    [InlineData(Standing.SoleTenantOwnerSession, Write.Create)]
    [InlineData(Standing.SoleTenantOwnerSession, Write.Rename)]
    [InlineData(Standing.SoleTenantOwnerSession, Write.Repermission)]
    [InlineData(Standing.SoleTenantOwnerSession, Write.Delete)]
    [InlineData(Standing.SoleTenantApiSecret, Write.Create)]
    [InlineData(Standing.SoleTenantApiSecret, Write.Rename)]
    [InlineData(Standing.SoleTenantApiSecret, Write.Repermission)]
    [InlineData(Standing.SoleTenantApiSecret, Write.Delete)]
    [InlineData(Standing.SoleTenantOwnerBesideADemoTenant, Write.Create)]
    [InlineData(Standing.SoleTenantOwnerBesideADemoTenant, Write.Rename)]
    [InlineData(Standing.SoleTenantOwnerBesideADemoTenant, Write.Repermission)]
    [InlineData(Standing.SoleTenantOwnerBesideADemoTenant, Write.Delete)]
    public async Task A_caller_with_standing_over_the_instance_writes_a_global_role(Standing standing, Write write)
    {
        await using var conn = await OpenPostgresAsync();
        var tenantA = await AuthTestHelpers.GetTenantIdAsync(conn);
        var slugA = await SlugAsync(conn, tenantA);
        var baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);
        var target = new Target(Guid.Empty, Readable, Readable, Seeded: false);
        var readable = await PermissionsOfAsync(conn, Readable);
        Guid? tenantB = null;
        try
        {
            if (standing is not (Standing.SoleTenantOwnerSession or Standing.SoleTenantApiSecret))
                tenantB = await AuthTestHelpers.SeedTenantAsync(Fixture, $"roles-b-{Guid.NewGuid():N}"[..20], "Roles B");
            if (standing == Standing.SoleTenantOwnerBesideADemoTenant)
                await conn.ExecuteAsync("UPDATE tenants SET is_demo = true WHERE id = @id;", ("id", tenantB!.Value));

            target = await SeedTargetAsync(conn, write);
            using var caller = standing switch
            {
                Standing.PlatformAdminSession => await PlatformAdminAsync(conn, tenantA, slugA, baseDomain),
                Standing.InstanceKey => InstanceKey(slugA, baseDomain),
                Standing.SoleTenantOwnerSession or Standing.SoleTenantOwnerBesideADemoTenant =>
                    AuthTestHelpers.CreateTenantBearerClient(Fixture, slugA, baseDomain,
                        await AuthTestHelpers.IssueSessionTokenAsync(Fixture, await OwnerAsync(conn, tenantA, "Second Owner"))),
                _ => ApiSecret(slugA, baseDomain),
            };

            var response = await SendAsync(caller, write, target);

            response.StatusCode.Should().Be(write switch
            {
                Write.Create => HttpStatusCode.Created,
                Write.Delete => HttpStatusCode.NoContent,
                _ => HttpStatusCode.OK,
            }, await response.Content.ReadAsStringAsync());
            switch (write)
            {
                case Write.Create:
                    (await AuthTestHelpers.RoleCountAsync(conn, target.Name)).Should().Be(1);
                    break;
                case Write.Rename:
                    (await AuthTestHelpers.RoleCountAsync(conn, target.Renamed)).Should().Be(1);
                    break;
                case Write.Repermission:
                    (await PermissionsOfAsync(conn, Readable)).Should().Be("[\"*\"]");
                    break;
                default:
                    (await AuthTestHelpers.RoleCountAsync(conn, target.Name)).Should().Be(0);
                    break;
            }
        }
        finally
        {
            await RestoreAsync(conn, target, readable);
            if (tenantB is { } extra)
                await RemoveTenantAsync(conn, extra, holderId: null);
        }
    }

    private sealed record Target(Guid Id, string Name, string Renamed, bool Seeded);

    /// <summary>
    /// The role each write acts on: a fresh name to create, a non-system role to rename or delete,
    /// and the system role every identity provider assigns by default to re-permission.
    /// </summary>
    private static async Task<Target> SeedTargetAsync(NpgsqlConnection conn, Write write)
    {
        var name = $"global-{Guid.NewGuid():N}";
        var renamed = $"renamed-{Guid.NewGuid():N}";
        switch (write)
        {
            case Write.Repermission:
                return new Target(await AuthTestHelpers.RoleIdAsync(conn, Readable), Readable, Readable, Seeded: false);
            case Write.Create:
                return new Target(Guid.Empty, name, renamed, Seeded: false);
            default:
                var id = Guid.CreateVersion7();
                await conn.ExecuteAsync("""
                    INSERT INTO roles (id, name, description, permissions, is_system_role, created_at, updated_at)
                    VALUES (@id, @name, 'Instance-wide', '["api:entries:read"]'::jsonb, false, now(), now());
                    """,
                    ("id", id), ("name", name));
                return new Target(id, name, renamed, Seeded: true);
        }
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Write write, Target target) => write switch
    {
        Write.Create => client.PostAsJsonAsync(Roles, new { name = target.Name, permissions = new[] { "api:entries:read" } }),
        Write.Rename => client.PutAsJsonAsync(Roles,
            new { id = target.Id.ToString(), name = target.Renamed, permissions = new[] { "api:entries:read" } }),
        Write.Repermission => client.PutAsJsonAsync(Roles,
            new { id = target.Id.ToString(), name = target.Name, permissions = new[] { Scope.FullAccess } }),
        _ => client.DeleteAsync($"{Roles}/{target.Id}"),
    };

    private static async Task RestoreAsync(NpgsqlConnection conn, Target target, string readable)
    {
        await conn.ExecuteAsync("""
            DELETE FROM subject_roles WHERE role_id IN (SELECT id FROM roles WHERE name IN (@name, @renamed) AND name <> @readable);
            DELETE FROM roles WHERE name IN (@name, @renamed) AND name <> @readable;
            UPDATE roles SET permissions = @permissions::jsonb WHERE name = @readable;
            """,
            ("name", target.Name), ("renamed", target.Renamed), ("readable", Readable), ("permissions", readable));
    }

    /// <summary>Removes a tenant the test added, with the global role a subject of it was given.</summary>
    private static async Task RemoveTenantAsync(NpgsqlConnection conn, Guid tenantId, Guid? holderId)
    {
        if (holderId is { } holder)
            await conn.ExecuteAsync("DELETE FROM subject_roles WHERE subject_id = @id;", ("id", holder));
        await conn.ExecuteAsync("DELETE FROM tenants WHERE id = @id;", ("id", tenantId));
    }

    /// <summary>
    /// A <c>noc_</c> direct grant with full access, minted by a platform admin on the tenant. A direct
    /// grant does not carry the platform-admin flag (<see cref="PlatformAdminCredentials"/>).
    /// </summary>
    private async Task<HttpClient> PlatformAdminDirectGrantAsync(
        NpgsqlConnection conn, Guid tenantId, string slug, string baseDomain)
    {
        using var session = await PlatformAdminAsync(conn, tenantId, slug, baseDomain);
        var created = await session.PostAsJsonAsync("/api/auth/direct-grants",
            new { label = "roles-standing", scopes = new[] { Scope.FullAccess } });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        return AuthTestHelpers.CreateTenantBearerClient(Fixture, slug, baseDomain, token);
    }

    private async Task<HttpClient> PlatformAdminAsync(NpgsqlConnection conn, Guid tenantId, string slug, string baseDomain)
    {
        var (adminId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Roles Platform Admin");
        await conn.ExecuteAsync("UPDATE subjects SET is_platform_admin = true WHERE id = @id;", ("id", adminId));
        return AuthTestHelpers.CreateTenantBearerClient(
            Fixture, slug, baseDomain, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, adminId));
    }

    /// <summary>A member holding the tenant's owner role, who is not a platform admin.</summary>
    private static async Task<Guid> OwnerAsync(NpgsqlConnection conn, Guid tenantId, string name)
    {
        var (ownerId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, name);
        await conn.ExecuteAsync("""
            INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id)
            SELECT gen_random_uuid(), id, @roleId FROM tenant_members WHERE tenant_id = @tenantId AND subject_id = @subjectId;
            """,
            ("roleId", await AuthTestHelpers.GetTenantRoleIdAsync(conn, tenantId, "owner")),
            ("tenantId", tenantId), ("subjectId", ownerId));
        return ownerId;
    }

    private HttpClient ApiSecret(string slug, string baseDomain)
    {
        var client = AuthTestHelpers.CreateTenantClient(Fixture, slug, baseDomain);
        client.DefaultRequestHeaders.Add("api-secret", TestApiSecret);
        return client;
    }

    private HttpClient InstanceKey(string slug, string baseDomain)
    {
        var client = AuthTestHelpers.CreateTenantClient(Fixture, slug, baseDomain);
        client.DefaultRequestHeaders.Add("X-Instance-Key", HashUtils.Sha256Hex(ApiIntegrationTestFixture.InstanceKey));
        client.DefaultRequestHeaders.Add("X-Instance-Service", "nocturne-billing");
        return client;
    }

    /// <summary>The role names and permissions a session minted now for the subject carries.</summary>
    private async Task<string> SessionAuthorityAsync(Guid subjectId)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(
            await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId));
        return string.Join("\n", token.Claims
            .Where(c => c.Type is "role" or ClaimTypes.Role or "permission")
            .Select(c => $"{c.Type}={c.Value}")
            .Order(StringComparer.Ordinal));
    }

    private static async Task<string> RolesSnapshotAsync(NpgsqlConnection conn)
    {
        var rows = await conn.QueryAsync(
            "SELECT id, name, permissions::text, is_system_role FROM roles ORDER BY id;",
            reader => $"{reader.GetGuid(0)} {reader.GetString(1)} {reader.GetString(2)} {reader.GetBoolean(3)}");
        return string.Join("\n", rows);
    }

    private static async Task<string> SlugAsync(NpgsqlConnection conn, Guid tenantId)
    {
        return await conn.ScalarAsync<string>("SELECT slug FROM tenants WHERE id = @id;", ("id", tenantId));
    }

    private static async Task<string> PermissionsOfAsync(NpgsqlConnection conn, string name)
    {
        return await conn.ScalarAsync<string>("SELECT permissions::text FROM roles WHERE name = @name;", ("name", name));
    }

    private static async Task<string> ShapeOfAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => h.Key != "Date")
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}");
        var body = TraceId().Replace(await response.Content.ReadAsStringAsync(), "\"traceId\":\"\"");
        return $"{(int)response.StatusCode}\n{string.Join("\n", headers)}\n\n{body}";
    }

    [GeneratedRegex("\"traceId\":\"[^\"]*\"")]
    private static partial Regex TraceId();
}
