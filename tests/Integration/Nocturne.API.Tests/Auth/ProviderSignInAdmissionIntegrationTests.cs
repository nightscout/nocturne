using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// An identity-provider sign-in on a tenant host decides admission before it writes anything, so a
/// refused sign-in leaves no subject and no identity link behind.
/// </summary>
/// <remarks>
/// The provider redirect, the code exchange and the ID-token validation of
/// <c>GET /api/auth/oidc/callback</c> need a live provider and are not driven. Each test calls
/// <see cref="OidcAuthService.CompleteLoginAsync"/> with the claims and the resolved tenant the
/// callback passes it, in a scope whose tenant is resolved as the callback's request would be,
/// against Postgres under the app role. The controller's audit row and redirect
/// are not exercised here.
/// </remarks>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class ProviderSignInAdmissionIntegrationTests : ApiIntegrationTestBase
{
    private readonly string _issuer = $"https://admission-{Guid.NewGuid():N}.example";
    private Guid _providerId;
    private string _defaultRole = string.Empty;

    public ProviderSignInAdmissionIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var conn = await OpenAsync();
        _defaultRole = await ScalarAsync<string>(conn, "SELECT name FROM roles ORDER BY name LIMIT 1;");
        _providerId = Guid.CreateVersion7();
        await ExecuteAsync(conn, """
            INSERT INTO oidc_providers (id, name, issuer_url, client_id, is_enabled, display_order, scopes, claim_mappings, default_roles, created_at, updated_at)
            VALUES (@provider, 'Admission IdP', @issuer, @client, true, 0, '["openid"]'::jsonb, '{}'::jsonb, @roles::jsonb, now(), now());
            """,
            ("provider", _providerId), ("issuer", _issuer), ("client", $"client-{_providerId:N}"),
            ("roles", $"[\"{_defaultRole}\"]"));
    }

    public override async Task DisposeAsync()
    {
        await using (var conn = await OpenAsync())
        {
            await ExecuteAsync(conn, """
                CREATE TEMP TABLE admission_subjects AS
                    SELECT subject_id FROM subject_oidc_identities WHERE provider_id = @provider;
                SELECT set_config('app.current_tenant_id', @tenantText, false);
                DELETE FROM tenant_members WHERE subject_id IN (SELECT subject_id FROM admission_subjects);
                DELETE FROM refresh_tokens WHERE subject_id IN (SELECT subject_id FROM admission_subjects);
                DELETE FROM subject_roles WHERE subject_id IN (SELECT subject_id FROM admission_subjects);
                DELETE FROM subject_oidc_identities WHERE provider_id = @provider;
                DELETE FROM subjects WHERE id IN (SELECT subject_id FROM admission_subjects);
                DELETE FROM oidc_providers WHERE id = @provider;
                """,
                ("provider", _providerId), ("tenantText", Fixture.TenantId.ToString()));
        }

        await base.DisposeAsync();
    }

    [Fact]
    public async Task A_sign_in_refused_for_lack_of_membership_leaves_no_subject_and_no_identity_link()
    {
        var result = await SignInAsync("stranger", Fixture.TenantId);

        result.IsAccessDenied.Should().BeTrue();
        result.Tokens.Should().BeNull();
        (await IdentityLinkCountAsync("stranger")).Should().Be(0);
        (await SubjectsNamedAsync("stranger")).Should().Be(0);
    }

    [Fact]
    public async Task A_sign_in_refused_for_lack_of_membership_leaves_an_existing_subject_as_it_was()
    {
        var existing = await CreateFromProviderAsync("known", name: "Known Name");

        var result = await SignInAsync("known", Fixture.TenantId, name: "Changed Name");

        result.IsAccessDenied.Should().BeTrue();
        result.SubjectId.Should().Be(existing);
        (await SubjectsNamedAsync("Known Name")).Should().Be(1);
        (await SubjectsNamedAsync("Changed Name")).Should().Be(0);
    }

    [Fact]
    public async Task A_first_time_identity_holding_a_valid_invite_gets_a_subject_with_the_providers_default_roles()
    {
        var token = await SeedInviteAsync();

        var result = await SignInAsync("invitee", Fixture.TenantId, returnUrl: $"/join?token={token}");

        result.Success.Should().BeTrue(result.ErrorDescription);
        var subjectId = result.SubjectId!.Value;
        (await LinkedSubjectAsync("invitee")).Should().Be(subjectId);
        (await GlobalRolesAsync(subjectId)).Should().Contain(_defaultRole);
    }

    [Fact]
    public async Task An_invite_admits_the_subject_an_earlier_sign_in_left_for_the_identity()
    {
        var leftover = await CreateFromProviderAsync("returning", name: "returning");
        var token = await SeedInviteAsync();

        var result = await SignInAsync("returning", Fixture.TenantId, returnUrl: $"/join?token={token}");

        result.Success.Should().BeTrue(result.ErrorDescription);
        result.SubjectId.Should().Be(leftover);
        (await IdentityLinkCountAsync("returning")).Should().Be(1);
    }

    [Fact]
    public async Task A_member_signs_in_as_the_subject_the_identity_is_linked_to()
    {
        var member = await CreateFromProviderAsync("member", name: "member");
        await using (var conn = await OpenAsync())
        {
            await ExecuteAsync(conn, """
                SELECT set_config('app.current_tenant_id', @tenantText, false);
                INSERT INTO tenant_members (id, tenant_id, subject_id, sys_created_at, sys_updated_at)
                VALUES (gen_random_uuid(), @tenant, @subject, now(), now());
                """,
                ("tenantText", Fixture.TenantId.ToString()), ("tenant", Fixture.TenantId), ("subject", member));
        }

        var result = await SignInAsync("member", Fixture.TenantId);

        result.Success.Should().BeTrue(result.ErrorDescription);
        result.SubjectId.Should().Be(member);
    }

    /// <summary>
    /// A tenant-creation invite is redeemed by a caller signed in on the apex, so a first-time
    /// identity signing in there still gets a subject and a session.
    /// </summary>
    [Fact]
    public async Task On_the_apex_a_first_time_identity_gets_a_subject_and_a_session()
    {
        var result = await SignInAsync("apex-newcomer", currentTenantId: null);

        result.Success.Should().BeTrue(result.ErrorDescription);
        result.Tokens.Should().NotBeNull();
        (await LinkedSubjectAsync("apex-newcomer")).Should().Be(result.SubjectId!.Value);
        (await GlobalRolesAsync(result.SubjectId!.Value)).Should().Contain(_defaultRole);
    }

    private async Task<OidcCallbackResult> SignInAsync(
        string externalId, Guid? currentTenantId, string returnUrl = "/", string? name = null)
    {
        using var scope = Fixture.Services.CreateScope();
        if (currentTenantId is { } tenantId)
        {
            scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
                new TenantContext(tenantId, "admission", "Admission", true, false));
        }

        var service = (OidcAuthService)scope.ServiceProvider.GetRequiredService<IOidcAuthService>();
        var provider = await scope.ServiceProvider.GetRequiredService<IOidcProviderService>()
            .GetProviderByIdAsync(_providerId);

        return await service.CompleteLoginAsync(
            new OidcAuthService.OidcStateData
            {
                Intent = "login",
                ProviderId = _providerId,
                ReturnUrl = returnUrl,
                Nonce = "nonce",
                TenantSlug = null,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            },
            provider!,
            new OidcAuthService.OidcIdTokenClaims { Sub = externalId, Name = name ?? externalId },
            currentTenantId,
            ipAddress: null,
            userAgent: null);
    }

    /// <summary>A subject made for the identity the way a sign-in that is admitted makes one.</summary>
    private async Task<Guid> CreateFromProviderAsync(string externalId, string name)
    {
        using var scope = Fixture.Services.CreateScope();
        var subject = await scope.ServiceProvider.GetRequiredService<ISubjectService>()
            .FindOrCreateFromOidcAsync(_providerId, externalId, _issuer, name: name);
        return subject.Id;
    }

    private async Task<string> SeedInviteAsync()
    {
        await using var conn = await OpenAsync();
        var token = $"invite-{Guid.NewGuid():N}";
        var viewerRoleId = await AuthTestHelpers.GetTenantRoleIdAsync(conn, Fixture.TenantId, RoleSeeds.Viewer);
        await ExecuteAsync(conn, """
            SELECT set_config('app.current_tenant_id', @tenantText, false);
            INSERT INTO member_invites (id, tenant_id, created_by_subject_id, token_hash, role_ids, limit_to_24_hours, expires_at, use_count, created_at)
            VALUES (@inviteId, @tenantId, @ownerId, @hash, @roleIds::jsonb, false, now() + interval '1 day', 0, now());
            """,
            ("tenantText", Fixture.TenantId.ToString()), ("inviteId", Guid.CreateVersion7()),
            ("tenantId", Fixture.TenantId), ("ownerId", Fixture.OwnerSubjectId),
            ("hash", HashUtils.Sha256Hex(token)), ("roleIds", $"[\"{viewerRoleId}\"]"));
        return token;
    }

    private async Task<long> IdentityLinkCountAsync(string externalId)
    {
        await using var conn = await OpenAsync();
        return await ScalarAsync<long>(conn,
            "SELECT count(*) FROM subject_oidc_identities WHERE issuer = @issuer AND oidc_subject_id = @sub;",
            ("issuer", _issuer), ("sub", externalId));
    }

    private async Task<Guid> LinkedSubjectAsync(string externalId)
    {
        await using var conn = await OpenAsync();
        return await ScalarAsync<Guid>(conn,
            "SELECT subject_id FROM subject_oidc_identities WHERE issuer = @issuer AND oidc_subject_id = @sub;",
            ("issuer", _issuer), ("sub", externalId));
    }

    private async Task<long> SubjectsNamedAsync(string name)
    {
        await using var conn = await OpenAsync();
        return await ScalarAsync<long>(conn, "SELECT count(*) FROM subjects WHERE name = @name;", ("name", name));
    }

    private async Task<List<string>> GlobalRolesAsync(Guid subjectId)
    {
        using var scope = Fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISubjectService>().GetSubjectRolesAsync(subjectId);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
