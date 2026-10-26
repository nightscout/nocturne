using System.Net;
using System.Security.Cryptography;
using System.Web;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// The Apple refresh token kept on a linked identity: encrypted at rest, kept only for Apple,
/// and revoked at the provider's revocation endpoint with a signed client secret.
/// </summary>
public class OidcProviderGrantServiceTests : IDisposable
{
    private const string Issuer = "https://appleid.apple.com";
    private const string RevokeEndpoint = "https://appleid.apple.com/auth/revoke";

    private readonly NocturneDbContext _db = TestDbContextFactory.CreateInMemoryContext();
    private readonly Mock<IOidcProviderService> _providers = new();
    private readonly RevocationEndpoint _endpoint = new();
    private readonly OidcProviderGrantService _service;
    private readonly OidcProvider _apple;

    public OidcProviderGrantServiceTests()
    {
        _apple = new OidcProvider
        {
            Id = Guid.NewGuid(),
            Name = "Apple",
            ProviderType = OidcProviderType.Apple,
            IssuerUrl = Issuer,
            ClientId = "run.nocturne.signin",
            ClientSecret = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem(),
            Apple = new AppleProviderSettings { TeamId = "TEAM123456", KeyId = "KEY1234567" },
        };
        _providers.Setup(p => p.GetProviderByIdAsync(_apple.Id)).ReturnsAsync(_apple);
        _providers.Setup(p => p.GetDiscoveryDocumentAsync(_apple.Id, It.IsAny<bool>()))
            .ReturnsAsync(new OidcDiscoveryDocument { Issuer = Issuer, RevocationEndpoint = RevokeEndpoint });

        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient("OidcProvider")).Returns(() => new HttpClient(_endpoint));

        _service = new OidcProviderGrantService(
            _db.AsFactory(), _providers.Object, http.Object, new EphemeralDataProtectionProvider(),
            TimeProvider.System, NullLogger<OidcProviderGrantService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task<(Guid SubjectId, Guid IdentityId)> SeedIdentityAsync(string sub = "001234.apple-user")
    {
        var subjectId = Guid.CreateVersion7();
        var identityId = Guid.CreateVersion7();
        _db.Subjects.Add(new SubjectEntity { Id = subjectId, Name = "ada", IsActive = true });
        _db.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
        {
            Id = identityId,
            SubjectId = subjectId,
            ProviderId = _apple.Id,
            OidcSubjectId = sub,
            Issuer = Issuer,
            LinkedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return (subjectId, identityId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Keep_StoresTheTokenEncrypted_AndCaptureReadsItBack()
    {
        var (subjectId, _) = await SeedIdentityAsync();

        await _service.KeepAsync(_apple, "001234.apple-user", "apple-refresh");

        _db.ChangeTracker.Clear();
        var stored = _db.SubjectOidcIdentities.Single().ProviderRefreshTokenEncrypted;
        stored.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString(stored!).Should().NotContain("apple-refresh");
        (await _service.CaptureAsync(subjectId)).Should().Equal(new HeldProviderGrant(_apple.Id, "apple-refresh"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Keep_IgnoresProvidersWhoseGrantsAreNotRevoked()
    {
        await SeedIdentityAsync();
        var keycloak = new OidcProvider { Id = Guid.NewGuid(), IssuerUrl = Issuer, ProviderType = OidcProviderType.Oidc };

        await _service.KeepAsync(keycloak, "001234.apple-user", "refresh");

        _db.ChangeTracker.Clear();
        _db.SubjectOidcIdentities.Single().ProviderRefreshTokenEncrypted.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Capture_NarrowsToOneIdentity()
    {
        var (subjectId, identityId) = await SeedIdentityAsync();
        await _service.KeepAsync(_apple, "001234.apple-user", "apple-refresh");

        (await _service.CaptureAsync(subjectId, identityId)).Should().HaveCount(1);
        (await _service.CaptureAsync(subjectId, Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Revoke_PostsTheTokenWithASignedClientSecret()
    {
        await _service.RevokeAsync([new HeldProviderGrant(_apple.Id, "apple-refresh")]);

        var form = _endpoint.Form.Should().ContainSingle().Subject;
        form["token"].Should().Be("apple-refresh");
        form["token_type_hint"].Should().Be("refresh_token");
        form["client_id"].Should().Be("run.nocturne.signin");
        form["client_secret"].Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Revoke_SwallowsAProviderFailure()
    {
        _endpoint.Status = HttpStatusCode.BadRequest;

        var act = () => _service.RevokeAsync([new HeldProviderGrant(_apple.Id, "apple-refresh")]);

        await act.Should().NotThrowAsync();
    }

    private sealed class RevocationEndpoint : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<Dictionary<string, string>> Form { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.ToString() != RevokeEndpoint)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
            Form.Add(form.AllKeys.ToDictionary(k => k!, k => form[k]!));
            return new HttpResponseMessage(Status);
        }
    }
}
