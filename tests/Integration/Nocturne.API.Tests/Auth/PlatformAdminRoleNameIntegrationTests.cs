using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Platform-admin standing comes from the subject's platform-admin flag alone. A global role that
/// happens to be named <c>platform_admin</c> is a role like any other, and the V2 roles API names no
/// role that.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public partial class PlatformAdminRoleNameIntegrationTests : ApiIntegrationTestBase
{
    private const string PlatformAdminRole = "platform_admin";
    private const string PlatformAdminEndpoint = "/api/v4/admin/tenants";

    public PlatformAdminRoleNameIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public enum Credential
    {
        Session,
        OAuthAccessToken,
    }

    /// <summary>
    /// The role row and its assignment are written by SQL: the only product paths that assign a global
    /// role to a person are setup, the Public subject and a provider's configured default roles.
    /// </summary>
    [Theory]
    [InlineData(Credential.Session)]
    [InlineData(Credential.OAuthAccessToken)]
    public async Task A_member_holding_a_global_role_named_platform_admin_is_refused_as_any_member_is(
        Credential credential)
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (holderId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Role Name Holder");
        var (memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Role Name Member");
        var roleId = Guid.CreateVersion7();

        await conn.ExecuteAsync("""
            INSERT INTO roles (id, name, description, permissions, is_system_role, created_at, updated_at)
            VALUES (@roleId, @name, 'Shares the platform-admin name', '[]'::jsonb, false, now(), now());
            INSERT INTO subject_roles (subject_id, role_id, assigned_at) VALUES (@holderId, @roleId, now());
            """,
            ("roleId", roleId), ("name", PlatformAdminRole), ("holderId", holderId));
        try
        {
            using var holder = AuthTestHelpers.CreateBearerClient(Fixture, await TokenForAsync(holderId, credential));
            using var member = AuthTestHelpers.CreateBearerClient(Fixture, await TokenForAsync(memberId, credential));

            var refused = await ShapeOfAsync(await member.GetAsync(PlatformAdminEndpoint));
            refused.Should().StartWith("403");

            (await ShapeOfAsync(await holder.GetAsync(PlatformAdminEndpoint))).Should().Be(refused);
        }
        finally
        {
            await conn.ExecuteAsync("""
                DELETE FROM subject_roles WHERE role_id = @roleId;
                DELETE FROM roles WHERE id = @roleId;
                """,
                ("roleId", roleId));
        }
    }

    [Theory]
    [InlineData("platform_admin")]
    [InlineData("Platform_Admin")]
    public async Task A_global_role_cannot_be_created_with_the_platform_admin_name(string name)
    {
        await using var conn = await OpenPostgresAsync();
        using var owner = await OwnerAsync(conn);
        try
        {
            var invalid = await RefusalOfAsync(await owner.PostAsJsonAsync(
                "/api/v2/authorization/roles", new { name = "", permissions = Array.Empty<string>() }));

            var response = await owner.PostAsJsonAsync(
                "/api/v2/authorization/roles", new { name, permissions = Array.Empty<string>() });

            (await RefusalOfAsync(response)).Should().Be(invalid);
            (await AuthTestHelpers.RoleCountAsync(conn, name)).Should().Be(0);
        }
        finally
        {
            await conn.ExecuteAsync("DELETE FROM roles WHERE name = @name;", ("name", name));
        }
    }

    [Fact]
    public async Task A_global_role_cannot_be_renamed_to_the_platform_admin_name()
    {
        await using var conn = await OpenPostgresAsync();
        using var owner = await OwnerAsync(conn);
        var original = $"rostered-{Guid.NewGuid():N}";
        try
        {
            var created = await owner.PostAsJsonAsync(
                "/api/v2/authorization/roles", new { name = original, permissions = Array.Empty<string>() });
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var id = (await AuthTestHelpers.RoleIdAsync(conn, original)).ToString();

            var invalid = await RefusalOfAsync(await owner.PutAsJsonAsync(
                "/api/v2/authorization/roles", new { id, name = "", permissions = Array.Empty<string>() }));

            var response = await owner.PutAsJsonAsync(
                "/api/v2/authorization/roles", new { id, name = PlatformAdminRole, permissions = Array.Empty<string>() });

            (await RefusalOfAsync(response)).Should().Be(invalid);
            (await AuthTestHelpers.RoleCountAsync(conn, original)).Should().Be(1);
        }
        finally
        {
            await conn.ExecuteAsync("DELETE FROM roles WHERE name IN (@original, @reserved);",
                ("original", original), ("reserved", PlatformAdminRole));
        }
    }

    private async Task<string> TokenForAsync(Guid subjectId, Credential credential)
    {
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        if (credential == Credential.Session)
            return session;

        using var signedIn = AuthTestHelpers.CreateBearerClient(Fixture, session);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(signedIn);
        return (await AuthTestHelpers.ExecutePkceFlowAsync(signedIn, clientId)).AccessToken;
    }

    private async Task<HttpClient> OwnerAsync(NpgsqlConnection conn)
    {
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (ownerId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Role Name Owner");
        return AuthTestHelpers.CreateBearerClient(Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, ownerId));
    }

    /// <summary>
    /// Status, content type and the fields a validation refusal names, without the messages.
    /// </summary>
    private static async Task<string> RefusalOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var fields = body.Length > 0
            && JsonSerializer.Deserialize<JsonElement>(body) is { ValueKind: JsonValueKind.Object } json
            && json.TryGetProperty("errors", out var errors)
                ? string.Join(",", errors.EnumerateObject().Select(e => e.Name).Order(StringComparer.Ordinal))
                : "";
        return $"{(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType} [{fields}]";
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
