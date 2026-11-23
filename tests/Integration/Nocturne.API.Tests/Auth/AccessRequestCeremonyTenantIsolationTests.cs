using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// The anonymous passkey access-request ceremony on Postgres, with row level security on and two
/// tenants: the request belongs to the tenant whose host the ceremony ran on, and only that
/// tenant's owner is told of it, sees it and can decide it.
/// </summary>
/// <remarks>
/// The ceremony runs through the API's own services in a scope resolved to tenant A, as a request
/// on A's host is. Only the WebAuthn verification is stubbed: it stores a credential through the
/// real repository, as the real one does once the attestation checks out.
/// </remarks>
[Collection(ApiIntegrationShards.One)]
[Trait("Category", "Integration")]
public class AccessRequestCeremonyTenantIsolationTests : ApiIntegrationTestBase
{
    private string _baseDomain = null!;
    private Guid _ownerA;
    private string _ownerAToken = null!;
    private Guid _tenantB;
    private string _slugB = null!;
    private string _ownerBToken = null!;
    private Guid _roleB;

    public AccessRequestCeremonyTenantIsolationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        (_ownerA, _ownerAToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, Fixture.TenantId, "Owner A");

        _slugB = $"tenant-b-{Guid.NewGuid():N}"[..20];
        _tenantB = await AuthTestHelpers.SeedTenantAsync(Fixture, _slugB, "Tenant B");
        (_, _ownerBToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantB, "Owner B");
        await SetTenantAsync(conn, _tenantB);
        _roleB = await AuthTestHelpers.GetTenantRoleIdAsync(conn, _tenantB, RoleSeeds.Viewer);
    }

    [Fact]
    public async Task ARequestMadeOnTenantAsHost_IsStoredAndToldUnderA_AndIsInvisibleToAndNotDecidableByTheOwnerOfB()
    {
        var displayName = $"Visitor {Guid.NewGuid():N}"[..20];

        var requesterId = await RunTheCeremonyOnTenantAAsync(displayName, "I am the school nurse");

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();

        using var ownerA = AuthTestHelpers.CreateTenantBearerClient(Fixture, ApiIntegrationTestFixture.TenantSlug, _baseDomain, _ownerAToken);
        var listedForA = await ownerA.GetFromJsonAsync<List<MembershipRequestDto>>("/api/v4/membership-requests");
        var request = listedForA.Should().ContainSingle(r => r.SubjectId == requesterId).Which;
        request.Message.Should().Be("I am the school nurse");
        (await RequestsVisibleUnderAsync(conn, Fixture.TenantId, request.Id)).Should().Be(1);
        (await RequestsVisibleUnderAsync(conn, _tenantB, request.Id)).Should().Be(0);
        (await RequestNotificationsAsync(conn, Fixture.TenantId, _ownerA, request.Id))
            .Should().Be(1, "the owner of the tenant the request was made on is told of it");
        (await RequestNotificationsAsync(conn, _tenantB, null, request.Id)).Should().Be(0);
        (await CredentialsOfAsync(conn, requesterId)).Should().Be(1);

        using var ownerB = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slugB, _baseDomain, _ownerBToken);
        (await ownerB.GetFromJsonAsync<List<MembershipRequestDto>>("/api/v4/membership-requests"))
            .Should().NotContain(r => r.SubjectId == requesterId);

        var approvedByB = await ownerB.PostAsJsonAsync(
            $"/api/v4/membership-requests/{request.Id}/approve", new { roleIds = new[] { _roleB } });
        approvedByB.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await approvedByB.Content.ReadFromJsonAsync<DecideMembershipRequestResult>())!.Error.Should().Be("Request not found.");

        (await ownerA.GetFromJsonAsync<List<MembershipRequestDto>>("/api/v4/membership-requests"))
            .Should().ContainSingle(r => r.Id == request.Id, "the owner of B decided nothing");
        (await MembershipsOfAsync(conn, requesterId)).Should().Be(0);
    }

    /// <summary>Runs the options and complete steps on tenant A's host; returns the requester.</summary>
    private async Task<Guid> RunTheCeremonyOnTenantAAsync(string displayName, string message)
    {
        using var scope = Fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));

        var credentials = services.GetRequiredService<IPasskeyCredentialRepository>();
        Guid? enrolling = null;
        var webAuthn = new Mock<IPasskeyService>();
        webAuthn.Setup(p => p.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), PasskeyRegistrationFlow.AccessRequest))
            .ReturnsAsync((Guid subjectId, string _, PasskeyRegistrationFlow _) =>
            {
                enrolling = subjectId;
                return new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "challenge-token");
            });
        webAuthn.Setup(p => p.FindRegistrationChallengeSubject("challenge-token", PasskeyRegistrationFlow.AccessRequest)).Returns(() => enrolling);
        webAuthn.Setup(p => p.CompleteRegistrationAsync(
                It.IsAny<string>(), "challenge-token", PasskeyRegistrationFlow.AccessRequest, It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .Returns(async (string _, string _, PasskeyRegistrationFlow _, Guid _, Guid expectedSubjectId, string? _) =>
            {
                expectedSubjectId.Should().Be(enrolling!.Value);
                var credentialId = await credentials.CreateAsync(
                    new NewPasskeyCredential(
                        expectedSubjectId, Guid.NewGuid().ToByteArray(), Guid.NewGuid().ToByteArray(), 0, [],
                        null, null, DateTime.UtcNow),
                    CancellationToken.None);
                return new PasskeyCredentialResult(credentialId, expectedSubjectId);
            });

        var controller = ActivatorUtilities.CreateInstance<PasskeyController>(services, webAuthn.Object);
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Host = new HostString($"{ApiIntegrationTestFixture.TenantSlug}.{_baseDomain}");
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        (await controller.AccessRequestOptions(new AccessRequestOptionsRequest { DisplayName = displayName, Message = message }))
            .Result.Should().BeOfType<OkObjectResult>();
        (await controller.AccessRequestComplete(
                new AccessRequestCompleteRequest
                {
                    DisplayName = displayName,
                    AttestationResponseJson = "{}",
                    ChallengeToken = "challenge-token",
                },
                services.GetRequiredService<IServiceScopeFactory>(),
                credentials))
            .Should().BeOfType<OkResult>();

        return enrolling!.Value;
    }

    private static async Task SetTenantAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> RequestsVisibleUnderAsync(NpgsqlConnection conn, Guid tenantId, Guid requestId)
    {
        await SetTenantAsync(conn, tenantId);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM membership_requests WHERE id = @id AND tenant_id = @tenant;";
        cmd.Parameters.AddWithValue("id", requestId);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> RequestNotificationsAsync(NpgsqlConnection conn, Guid tenantId, Guid? recipient, Guid requestId)
    {
        await SetTenantAsync(conn, tenantId);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*) FROM in_app_notifications
            WHERE tenant_id = @tenant AND type = 'membership.requested' AND source_id = @source AND (@recipient IS NULL OR user_id = @recipient);
            """;
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("source", requestId.ToString());
        cmd.Parameters.Add(new NpgsqlParameter("recipient", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)recipient?.ToString() ?? DBNull.Value });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> CredentialsOfAsync(NpgsqlConnection conn, Guid subjectId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM passkey_credentials WHERE subject_id = @subject;";
        cmd.Parameters.AddWithValue("subject", subjectId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> MembershipsOfAsync(NpgsqlConnection conn, Guid subjectId)
    {
        long total = 0;
        foreach (var tenantId in new[] { Fixture.TenantId, _tenantB })
        {
            await SetTenantAsync(conn, tenantId);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM tenant_members WHERE tenant_id = @tenant AND subject_id = @subject;";
            cmd.Parameters.AddWithValue("tenant", tenantId);
            cmd.Parameters.AddWithValue("subject", subjectId);
            total += (long)(await cmd.ExecuteScalarAsync())!;
        }

        return total;
    }
}
