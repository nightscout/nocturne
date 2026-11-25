using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Completing onboarding is tenant administration: the owner who finishes first-run setup from
/// their own sign-in completes it, and a credential without <c>tenant.settings</c> reads the 403
/// that names the scope.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class OnboardingCompletionScopeIntegrationTests : ApiIntegrationTestBase
{
    private const string CompletePath = "/api/auth/passkey/onboarding/complete";

    public OnboardingCompletionScopeIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task The_owner_completes_onboarding_from_their_own_sign_in()
    {
        var (completedAt, enrolledAt) = await ClearOnboardingAsync();
        try
        {
            using var owner = AuthTestHelpers.CreateBearerClient(
                Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, Fixture.OwnerSubjectId));
            var response = await owner.PostAsync(CompletePath, null);

            response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
            var status = await (await ApiClient.GetAsync("/api/auth/passkey/status"))
                .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
            status.GetProperty("onboardingCompleted").GetBoolean().Should().BeTrue();
        }
        finally
        {
            await RestoreOnboardingAsync(completedAt, enrolledAt);
        }
    }

    [Fact]
    public async Task A_credential_without_tenant_settings_reads_the_named_scope_refusal()
    {
        using var owner = AuthTestHelpers.CreateBearerClient(
            Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, Fixture.OwnerSubjectId));
        var minted = await owner.PostAsJsonAsync(
            "/api/auth/direct-grants",
            new { label = "no tenant settings", scopes = new[] { Scope.GlucoseReadWrite, Scope.TreatmentsReadWrite } });
        var mintedBody = await minted.Content.ReadAsStringAsync();
        minted.StatusCode.Should().Be(HttpStatusCode.OK, mintedBody);
        var token = JsonSerializer.Deserialize<JsonElement>(mintedBody).GetProperty("token").GetString()!;

        using var client = AuthTestHelpers.CreateBearerClient(Fixture, token);
        var response = await client.PostAsync(CompletePath, null);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        problem.GetProperty("code").GetString().Should().Be("missing_scope");
        problem.GetProperty("scopes").EnumerateArray().Select(s => s.GetString())
            .Should().Equal(Scope.TenantSettings);
    }

    /// <summary>
    /// Clears the fixture tenant's onboarding and returns what it held: the tenant is shared by the
    /// shard, and the fixture's cleanup resets neither column.
    /// </summary>
    private async Task<(object CompletedAt, object EnrolledAt)> ClearOnboardingAsync()
    {
        await using var conn = await OpenPostgresAsync();
        var before = (await conn.QueryAsync(
            "SELECT onboarding_completed_at, setup_hub_enrolled_at FROM tenants WHERE id = @id",
            reader => (reader.GetValue(0), reader.GetValue(1)), ("id", Fixture.TenantId))).Should().ContainSingle().Subject;

        (await conn.ExecuteAsync("UPDATE tenants SET onboarding_completed_at = NULL WHERE id = @id", ("id", Fixture.TenantId))).Should().Be(1);
        return before;
    }

    private async Task RestoreOnboardingAsync(object completedAt, object enrolledAt)
    {
        await using var conn = await OpenPostgresAsync();
        await conn.ExecuteAsync(
            "UPDATE tenants SET onboarding_completed_at = @completed, setup_hub_enrolled_at = @enrolled WHERE id = @id",
            ("completed", completedAt), ("enrolled", enrolledAt), ("id", Fixture.TenantId));
    }
}
