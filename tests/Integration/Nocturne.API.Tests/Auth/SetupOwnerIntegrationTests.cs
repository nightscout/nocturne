using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// First-run setup through the real API: setup makes the subject that completes it the owner, and
/// no other subject the instance already holds.
/// </summary>
/// <remarks>
/// The seeded tenant is put back to a fresh install for each test: its owner loses its passkey and
/// every active subject on the instance is deactivated (so owners are counted among active subjects),
/// since <c>subjects</c> is not tenant-scoped
/// and the fixture's cleanup does not empty it. <see cref="DisposeAsync"/> reactivates the subjects;
/// the fixture's cleanup before the next test restores the passkey and the memberships.
/// </remarks>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class SetupOwnerIntegrationTests : ApiIntegrationTestBase
{
    private const string Issuer = "https://setup-owner-idp.example";

    private readonly List<Guid> _deactivated = [];
    private Guid _providerId;

    public SetupOwnerIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var conn = await OpenPostgresAsync();
        _deactivated.AddRange(await conn.QueryAsync(
            """
            UPDATE subjects SET is_active = false
            WHERE is_active AND NOT is_system_subject AND NOT is_demo_subject
            RETURNING id;
            """,
            reader => reader.GetGuid(0)));

        _providerId = Guid.CreateVersion7();
        await conn.ExecuteAsync(
            """
            DELETE FROM passkey_credentials WHERE subject_id IN
                (SELECT subject_id FROM tenant_members WHERE tenant_id = @tenant);
            INSERT INTO oidc_providers (id, name, issuer_url, client_id, is_enabled, display_order, scopes, claim_mappings, default_roles, created_at, updated_at)
            VALUES (@provider, 'Setup owner IdP', @issuer, @client, true, 0, '["openid"]'::jsonb, '{}'::jsonb, '[]'::jsonb, now(), now());
            """,
            ("tenant", Fixture.TenantId), ("provider", _providerId), ("issuer", Issuer), ("client", $"client-{_providerId:N}"));
    }

    public override async Task DisposeAsync()
    {
        await using (var conn = await OpenPostgresAsync())
        await conn.ExecuteAsync(
            """
            UPDATE subjects SET is_active = false
            WHERE is_active AND NOT is_system_subject AND NOT is_demo_subject;
            UPDATE subjects SET is_active = true WHERE id = ANY(@restore);
            DELETE FROM subject_oidc_identities WHERE provider_id = @provider;
            DELETE FROM oidc_providers WHERE id = @provider;
            """,
            ("restore", _deactivated.ToArray()), ("provider", _providerId));

        await base.DisposeAsync();
    }

    [Fact]
    public async Task SetupMakesTheSubjectThatCompletesItTheOwner_NotOneThatSignedInEarlier()
    {
        var earlier = await SignInWithProviderAsync("someone-else");

        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");

        var owner = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;
        owner.Should().NotBe(earlier);
        (await MembershipCountAsync(earlier)).Should().Be(0);
        (await UsernameAsync(earlier)).Should().BeNull();
        (await GlobalRolesAsync(earlier)).Should().NotContain("admin");

        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        (await OwnerSubjectIdsAsync()).Should().Equal(owner);
    }

    [Fact]
    public async Task SetupResumedAfterAnAbandonedCeremony_KeepsTheSubjectItCreated()
    {
        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        var owner = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;

        var later = await SignInWithProviderAsync("someone-else");

        await StartOwnerSetupAsync(apex, "operator", "The Operator");

        (await OwnerSubjectIdsAsync()).Should().Equal(owner);
        (await MembershipCountAsync(later)).Should().Be(0);
    }

    /// <summary>
    /// What the identity provider's sign-in callback does for an identity it has not seen: the
    /// HTTP leg and the code exchange are not driven here, since they need a live provider.
    /// </summary>
    private async Task<Guid> SignInWithProviderAsync(string externalId)
    {
        using var scope = Fixture.Services.CreateScope();
        var subject = await scope.ServiceProvider.GetRequiredService<ISubjectService>()
            .FindOrCreateFromOidcAsync(_providerId, externalId, Issuer, name: externalId);
        return subject.Id;
    }

    private async Task<List<string>> GlobalRolesAsync(Guid subjectId)
    {
        using var scope = Fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISubjectService>().GetSubjectRolesAsync(subjectId);
    }

    private static async Task StartOwnerSetupAsync(HttpClient apex, string username, string displayName)
    {
        using var response = await apex.PostAsJsonAsync(
            "/api/v4/setup/owner/options", new { username, displayName });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
    }

    private async Task<List<Guid>> OwnerSubjectIdsAsync()
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.QueryAsync("""
            SELECT m.subject_id
            FROM tenant_members m
            JOIN tenant_member_roles mr ON mr.tenant_member_id = m.id
            JOIN tenant_roles r ON r.id = mr.tenant_role_id
            JOIN subjects s ON s.id = m.subject_id
            WHERE m.tenant_id = @tenant AND r.slug = 'owner' AND s.is_active
            ORDER BY m.subject_id;
            """, reader => reader.GetGuid(0), ("tenant", Fixture.TenantId));
    }

    private async Task<long> MembershipCountAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<long>("SELECT count(*) FROM tenant_members WHERE subject_id = @subject", ("subject", subjectId));
    }

    private async Task<string?> UsernameAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<string?>("SELECT username FROM subjects WHERE id = @subject", ("subject", subjectId));
    }
}
