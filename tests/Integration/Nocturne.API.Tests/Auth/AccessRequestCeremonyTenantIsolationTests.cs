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
using Nocturne.Tests.Shared.Infrastructure;
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
/// The ceremony runs through the API's own services in a scope resolved to one tenant, as a request
/// on that tenant's host is. Only the WebAuthn verification is stubbed: it stores a credential through the
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

        await using var conn = await OpenPostgresAsync();
        (_ownerA, _ownerAToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, Fixture.TenantId, "Owner A");

        _slugB = $"tenant-b-{Guid.NewGuid():N}"[..20];
        _tenantB = await AuthTestHelpers.SeedTenantAsync(Fixture, _slugB, "Tenant B");
        (_, _ownerBToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantB, "Owner B");
        await conn.PinTenantAsync(_tenantB);
        _roleB = await AuthTestHelpers.GetTenantRoleIdAsync(conn, _tenantB, RoleSeeds.Viewer);
    }

    [Fact]
    public async Task ARequestMadeOnTenantAsHost_IsStoredAndToldUnderA_AndIsInvisibleToAndNotDecidableByTheOwnerOfB()
    {
        var displayName = $"Visitor {Guid.NewGuid():N}"[..20];

        var requesterId = await RunTheCeremonyOnTenantAAsync(displayName, "I am the school nurse");

        await using var conn = await OpenPostgresAsync();

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

    [Fact]
    public async Task ANamePendingOnTenantA_RefusesThatNameOnA_AndNotOnB()
    {
        var displayName = $"Visitor {Guid.NewGuid():N}"[..20];
        await RunTheCeremonyOnTenantAAsync(displayName, "I am the school nurse");

        (await AccessRequestOptionsOnAsync(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, displayName))
            .Result.Should().BeOfType<ConflictObjectResult>("the request is pending on this tenant");
        (await AccessRequestOptionsOnAsync(_tenantB, _slugB, displayName))
            .Result.Should().BeOfType<OkObjectResult>("the request was filed on another tenant");
    }

    [Fact]
    public async Task ANamePendingOnTenantA_RefusesThatNameOnA_WhenTheRequestRunsOnAShareHost()
    {
        var displayName = $"Visitor {Guid.NewGuid():N}"[..20];
        await RunTheCeremonyOnTenantAAsync(displayName, "I am the school nurse");

        (await AccessRequestOptionsOnAsync(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, displayName, isShare: true))
            .Result.Should().BeOfType<ConflictObjectResult>(
                "a share host reads as a share, which is hidden the tenant's membership requests, and the name is still pending");
    }

    private Task<Guid> RunTheCeremonyOnTenantAAsync(string displayName, string message) =>
        RunTheCeremonyAsync(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, displayName, message);

    private async Task<ActionResult<PasskeyOptionsResponse>> AccessRequestOptionsOnAsync(
        Guid tenantId, string slug, string displayName, bool isShare = false)
    {
        using var scope = Fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<ITenantAccessor>().SetTenant(new TenantContext(tenantId, slug, "Integration", true, false));
        if (isShare)
        {
            services.GetRequiredService<ICategoryReadContext>().MarkShare();
        }

        var webAuthn = new Mock<IPasskeyService>();
        webAuthn.Setup(p => p.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), PasskeyRegistrationFlow.AccessRequest))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "challenge-token"));
        return await ControllerOn(services, webAuthn.Object, slug)
            .AccessRequestOptions(new AccessRequestOptionsRequest { DisplayName = displayName });
    }

    private PasskeyController ControllerOn(IServiceProvider services, IPasskeyService webAuthn, string slug)
    {
        var controller = ActivatorUtilities.CreateInstance<PasskeyController>(services, webAuthn);
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Host = new HostString($"{slug}.{_baseDomain}");
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>Runs the options and complete steps on a tenant's host; returns the requester.</summary>
    private async Task<Guid> RunTheCeremonyAsync(Guid tenantId, string slug, string displayName, string message)
    {
        using var scope = Fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<ITenantAccessor>().SetTenant(new TenantContext(tenantId, slug, "Integration", true, false));

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

        var controller = ControllerOn(services, webAuthn.Object, slug);

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

    private static async Task<long> RequestsVisibleUnderAsync(NpgsqlConnection conn, Guid tenantId, Guid requestId)
    {
        await conn.PinTenantAsync(tenantId);
        return await conn.ScalarAsync<long>("SELECT count(*) FROM membership_requests WHERE id = @id AND tenant_id = @tenant;", ("id", requestId), ("tenant", tenantId));
    }

    private static async Task<long> RequestNotificationsAsync(NpgsqlConnection conn, Guid tenantId, Guid? recipient, Guid requestId)
    {
        await conn.PinTenantAsync(tenantId);
        return await conn.ScalarAsync<long>(
            """
            SELECT count(*) FROM in_app_notifications
            WHERE tenant_id = @tenant AND type = 'membership.requested' AND source_id = @source
              AND (@recipient::text IS NULL OR user_id = @recipient::text);
            """,
            ("tenant", tenantId), ("source", requestId.ToString()), ("recipient", (object?)recipient?.ToString() ?? DBNull.Value));
    }

    private static async Task<long> CredentialsOfAsync(NpgsqlConnection conn, Guid subjectId)
    {
        return await conn.ScalarAsync<long>("SELECT count(*) FROM passkey_credentials WHERE subject_id = @subject;", ("subject", subjectId));
    }

    private async Task<long> MembershipsOfAsync(NpgsqlConnection conn, Guid subjectId)
    {
        long total = 0;
        foreach (var tenantId in new[] { Fixture.TenantId, _tenantB })
        {
            await conn.PinTenantAsync(tenantId);
            total += await conn.ScalarAsync<long>(
                "SELECT count(*) FROM tenant_members WHERE tenant_id = @tenant AND subject_id = @subject;",
                ("tenant", tenantId), ("subject", subjectId));
        }

        return total;
    }
}
