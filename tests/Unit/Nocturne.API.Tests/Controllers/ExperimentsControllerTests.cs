using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Covers the Nightscout <c>/api/v1/experiments/test</c> endpoint that Loop's connection
/// check relies on: a valid API secret must return 200 (Loop treats anything else as a
/// failed connection), and an unauthenticated request must return 401.
/// </summary>
public class ExperimentsControllerTests : IClassFixture<AuthenticationTestFactory>
{
    private readonly AuthenticationTestFactory _factory;

    public ExperimentsControllerTests(AuthenticationTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ExperimentsTest_ValidApiSecret_Returns200Ok()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "api-secret",
            ComputeSha1Hash(AuthenticationTestFactory.ApiSecret));

        var response = await client.GetAsync("/api/v1/experiments/test", CancellationToken.None);
        var content = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", content);
    }

    [Fact]
    public async Task ExperimentsTest_GranularUploaderToken_Returns200Ok()
    {
        // A minted uploader token (e.g. a Loop key from the setup wizard) carries granular
        // scopes like glucose.readwrite — not the legacy full-access "*". The connection probe
        // must accept it; gating on a broad RequireRead permission used to 403 these tokens.
        const string uploaderToken = "noc_granular-uploader-test-token";
        using (var ctx = _factory.Services
                   .GetRequiredService<IDbContextFactory<NocturneDbContext>>()
                   .CreateDbContext())
        {
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.NewGuid(),
                TenantId = TestDatabaseSeeder.TenantId,
                SubjectId = TestDatabaseSeeder.TestSubjectId,
                GrantType = OAuthGrantTypes.Direct,
                LegacySecretHash = TestDatabaseSeeder.Sha1Hex(uploaderToken),
                IsMigrated = false,
                Scopes =
                [
                    OAuthScopes.GlucoseReadWrite,
                    OAuthScopes.TreatmentsReadWrite,
                    OAuthScopes.DevicesReadWrite,
                ],
                Label = "Loop",
                CreatedAt = DateTime.UtcNow,
            });
            ctx.SaveChanges();
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("api-secret", ComputeSha1Hash(uploaderToken));

        var response = await client.GetAsync("/api/v1/experiments/test", CancellationToken.None);
        var content = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", content);
    }

    [Fact]
    public async Task ExperimentsTest_NoAuthentication_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/experiments/test", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string ComputeSha1Hash(string input)
    {
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }
}
