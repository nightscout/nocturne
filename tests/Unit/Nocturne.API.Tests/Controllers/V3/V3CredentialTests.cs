using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// Nightscout answers a missing or rejected credential on every V3 operation with 401 and a
/// <c>{status, message}</c> body. AAPS calls <c>/api/v3/status</c> first to check its credential, so a
/// 200 there makes a revoked token look connected.
/// </summary>
[Trait("Category", "Unit")]
public class V3CredentialTests : IClassFixture<AuthenticationTestFactory>
{
    private readonly AuthenticationTestFactory _factory;

    public V3CredentialTests(AuthenticationTestFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/v3/status")]
    [InlineData("/api/v3/lastModified")]
    [InlineData("/api/v3/entries")]
    public async Task WithoutACredential_Answers401WithTheV3Body(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        await ShouldBeTheV3Challenge(response);
    }

    [Theory]
    [InlineData("/api/v3/status")]
    [InlineData("/api/v3/lastModified")]
    public async Task WithARejectedToken_Answers401WithTheV3Body(string path)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        var response = await client.GetAsync(path);

        await ShouldBeTheV3Challenge(response);
    }

    [Theory]
    [InlineData("/api/v3/status")]
    [InlineData("/api/v3/lastModified")]
    public async Task WithAValidCredential_Answers200(string path)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("api-secret", TestDatabaseSeeder.Sha1Hex(AuthenticationTestFactory.ApiSecret));

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Version_StaysAnonymous()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v3/version");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task V1_KeepsItsOwnChallenge()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/entries");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("message", out _).Should().BeFalse();
        body.RootElement.GetProperty("title").GetString().Should().Be("Unauthorized");
    }

    [Fact]
    public async Task AMemberWithNoPermissions_IsForbiddenWithoutTheChallengeBody()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await SessionCookieAsync(RoleSeeds.Denied));

        var response = await client.GetAsync("/api/v3/status");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(V3ChallengeMessage);
        body.Should().NotContain("\"status\":401");
    }

    [Fact]
    public async Task V4_KeepsItsOwnChallenge()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v4/glucose/sensor");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("message", out _).Should().BeFalse();
        body.RootElement.GetProperty("title").GetString().Should().Be("Unauthorized");
    }

    private const string V3ChallengeMessage = "Missing or bad access token or JWT";

    private async Task<string> SessionCookieAsync(string roleSlug)
    {
        Guid subjectId;
        await using (var db = await _factory.Services
            .GetRequiredService<IDbContextFactory<NocturneDbContext>>()
            .CreateDbContextAsync())
        {
            db.TenantId = TestDatabaseSeeder.TenantId;
            subjectId = await TestDatabaseSeeder.SeedMemberAsync(db, TestDatabaseSeeder.TenantId, roleSlug);

            // A member with no passkey puts the tenant into recovery mode, which answers 503 to everyone.
            db.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.NewGuid(),
                SubjectId = subjectId,
                CredentialId = subjectId.ToByteArray(),
                PublicKey = [5, 6, 7, 8],
                SignCount = 0,
            });
            await db.SaveChangesAsync();
        }

        using var scope = _factory.Services.CreateScope();
        var session = await scope.ServiceProvider
            .GetRequiredService<ISessionService>()
            .IssueSessionAsync(subjectId, new SessionContext(DeviceDescription: "V3 credential test"));
        var cookieName = scope.ServiceProvider
            .GetRequiredService<IOptions<OidcOptions>>().Value.Cookie.AccessTokenName;

        return $"{cookieName}={session.AccessToken}";
    }

    private static async Task ShouldBeTheV3Challenge(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().NotBeEmpty();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().Should().Be(401);
        body.RootElement.GetProperty("message").GetString().Should().Be(V3ChallengeMessage);
        body.RootElement.EnumerateObject().Should().HaveCount(2);
    }
}
