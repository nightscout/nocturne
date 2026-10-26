using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Sign in with Apple through <see cref="OidcAuthService"/>: the form-POST redirect URI, the
/// relay that turns Apple's POST into the flow's GET callback, the signed client secret sent in
/// the form body, the one-time name, and the refresh token kept for revocation.
/// </summary>
public class OidcAuthServiceAppleTests
{
    private const string Issuer = "https://appleid.apple.com";
    private const string ServicesId = "run.nocturne.signin";
    private const string AppleCallback = "https://nocturne.example.com/api/auth/oidc/apple/callback";

    private static readonly Guid ProviderId = Guid.NewGuid();
    private static readonly Guid OidcProviderId = Guid.NewGuid();

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Mock<IOidcProviderService> _providers = new();
    private readonly Mock<ISubjectService> _subjects = new();
    private readonly Mock<ISessionService> _sessions = new();
    private readonly Mock<IOidcProviderGrantService> _grants = new();
    private readonly TokenEndpoint _tokenEndpoint = new();
    private readonly OidcAuthService _service;

    public OidcAuthServiceAppleTests()
    {
        _providers.Setup(p => p.GetProviderByIdAsync(ProviderId)).ReturnsAsync(AppleProvider());
        _providers.Setup(p => p.GetProviderByIdAsync(OidcProviderId)).ReturnsAsync(new OidcProvider
        {
            Id = OidcProviderId,
            Name = "Keycloak",
            IssuerUrl = "https://issuer.example",
            ClientId = "nocturne",
            IsEnabled = true,
        });
        _providers.Setup(p => p.GetDiscoveryDocumentAsync(It.IsAny<Guid>(), It.IsAny<bool>()))
            .ReturnsAsync(new OidcDiscoveryDocument
            {
                Issuer = Issuer,
                AuthorizationEndpoint = $"{Issuer}/auth/authorize",
                TokenEndpoint = $"{Issuer}/auth/token",
                RevocationEndpoint = $"{Issuer}/auth/revoke",
            });

        _subjects
            .Setup(s => s.FindOrCreateFromOidcAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new Nocturne.Core.Models.Authorization.Subject { Id = Guid.NewGuid(), Name = "Someone" });
        _subjects.Setup(s => s.GetSubjectPermissionsAsync(It.IsAny<Guid>())).ReturnsAsync([]);
        _subjects.Setup(s => s.GetSubjectRolesAsync(It.IsAny<Guid>())).ReturnsAsync([]);
        _sessions
            .Setup(s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900));

        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient("OidcProvider")).Returns(() => new HttpClient(_tokenEndpoint));

        _service = new OidcAuthService(
            _providers.Object,
            _subjects.Object,
            _sessions.Object,
            new Mock<IJwtService>().Object,
            new Mock<IRefreshTokenService>().Object,
            http.Object,
            new Mock<ITenantMemberService>().Object,
            new Mock<IMemberInviteService>().Object,
            new Mock<ITotpService>().Object,
            _grants.Object,
            new EphemeralDataProtectionProvider(),
            Options.Create(new OidcOptions()),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            NullLogger<OidcAuthService>.Instance);
    }

    private OidcProvider AppleProvider() => new()
    {
        Id = ProviderId,
        Name = "Apple",
        ProviderType = OidcProviderType.Apple,
        IssuerUrl = Issuer,
        ClientId = ServicesId,
        ClientSecret = _key.ExportPkcs8PrivateKeyPem(),
        Apple = new AppleProviderSettings { TeamId = "TEAM123456", KeyId = "KEY1234567" },
        Scopes = ["openid", "name", "email"],
        IsEnabled = true,
    };

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AuthorizationUrl_AsksForAFormPostToTheAppleCallback()
    {
        var request = await _service.GenerateAuthorizationUrlAsync(ProviderId);

        var query = HttpUtility.ParseQueryString(new Uri(request.AuthorizationUrl).Query);
        query["response_mode"].Should().Be("form_post");
        query["redirect_uri"].Should().Be(AppleCallback);
        query["scope"].Should().Be("openid name email");
        query["nonce"].Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AuthorizationUrl_ForOtherProviders_HasNoResponseMode()
    {
        var request = await _service.GenerateAuthorizationUrlAsync(OidcProviderId);

        HttpUtility.ParseQueryString(new Uri(request.AuthorizationUrl).Query)["response_mode"].Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Relay_SendsALoginToTheLoginCallback_WithCodeStateAndName()
    {
        var request = await _service.GenerateAuthorizationUrlAsync(ProviderId);

        var url = await _service.GetFormPostRelayUrlAsync(request.State, "the-code", null, AppleUser("Ada", "Lovelace"));

        url.Should().StartWith("/api/auth/oidc/callback?");
        var query = HttpUtility.ParseQueryString(url![url.IndexOf('?')..]);
        query["code"].Should().Be("the-code");
        query["state"].Should().Be(request.State);
        query["provider_user"].Should().NotBeNullOrEmpty();
        query["provider_user"].Should().NotContain("Ada", "the name travels protected");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Relay_SendsALinkToTheLinkCallback()
    {
        var request = await _service.GenerateLinkAuthorizationUrlAsync(ProviderId, Guid.NewGuid());

        var url = await _service.GetFormPostRelayUrlAsync(request.State, "the-code", null, null);

        url.Should().StartWith("/api/auth/oidc/link/callback?");
        HttpUtility.ParseQueryString(url![url.IndexOf('?')..])["provider_user"].Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Relay_SendsASetupToTheSetupCallback()
    {
        var request = await _service.GenerateSetupAuthorizationUrlAsync(ProviderId, Guid.NewGuid());

        var url = await _service.GetFormPostRelayUrlAsync(request.State, "the-code", null, null);

        url.Should().StartWith("/api/v4/setup/oidc/callback?");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Relay_CarriesAnErrorWithoutACode()
    {
        var request = await _service.GenerateAuthorizationUrlAsync(ProviderId);

        var url = await _service.GetFormPostRelayUrlAsync(request.State, null, "user_cancelled_authorize", null);

        var query = HttpUtility.ParseQueryString(url![url.IndexOf('?')..]);
        query["error"].Should().Be("user_cancelled_authorize");
        query["code"].Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Relay_RefusesAStateThisInstanceDidNotIssue()
    {
        (await _service.GetFormPostRelayUrlAsync("forged", "the-code", null, null)).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Relay_RefusesAStateForAProviderThatDoesNotFormPost()
    {
        var request = await _service.GenerateAuthorizationUrlAsync(OidcProviderId);

        (await _service.GetFormPostRelayUrlAsync(request.State, "the-code", null, null)).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Callback_SignsAClientSecretIntoTheFormBody_AndPresentsTheAppleRedirectUri()
    {
        var request = await _service.GenerateAuthorizationUrlAsync(ProviderId);
        _tokenEndpoint.Respond(IdToken(request.Nonce!), refreshToken: "apple-refresh");

        var result = await _service.HandleCallbackAsync("the-code", request.State, request.State);

        result.Success.Should().BeTrue();
        _tokenEndpoint.Authorization.Should().BeNull("Apple accepts only client_secret_post");
        var form = _tokenEndpoint.Form!;
        form["redirect_uri"].Should().Be(AppleCallback);
        form["client_id"].Should().Be(ServicesId);

        var secret = new JsonWebToken(form["client_secret"]);
        secret.Alg.Should().Be(SecurityAlgorithms.EcdsaSha256);
        secret.Kid.Should().Be("KEY1234567");
        secret.Issuer.Should().Be("TEAM123456");
        secret.Subject.Should().Be(ServicesId);
        secret.Audiences.Should().Equal("https://appleid.apple.com");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Callback_TakesTheNameFromTheRelay_AndKeepsTheRefreshToken()
    {
        var request = await _service.GenerateAuthorizationUrlAsync(ProviderId);
        var url = await _service.GetFormPostRelayUrlAsync(request.State, "the-code", null, AppleUser("Ada", "Lovelace"));
        var providerUser = HttpUtility.ParseQueryString(url![url.IndexOf('?')..])["provider_user"];
        _tokenEndpoint.Respond(IdToken(request.Nonce!), refreshToken: "apple-refresh");

        await _service.HandleCallbackAsync("the-code", request.State, request.State, providerUser: providerUser);

        _subjects.Verify(s => s.FindOrCreateFromOidcAsync(
            ProviderId, "001234.apple-user", Issuer, "x7@privaterelay.appleid.com", true, "Ada Lovelace",
            It.IsAny<IEnumerable<string>?>()));
        _grants.Verify(g => g.KeepAsync(
            It.Is<OidcProvider>(p => p.Id == ProviderId), "001234.apple-user", "apple-refresh", It.IsAny<CancellationToken>()));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Callback_IgnoresANameRelayedForAnotherSignIn()
    {
        var other = await _service.GenerateAuthorizationUrlAsync(ProviderId);
        var url = await _service.GetFormPostRelayUrlAsync(other.State, "c", null, AppleUser("Mallory", "X"));
        var foreignUser = HttpUtility.ParseQueryString(url![url.IndexOf('?')..])["provider_user"];

        var request = await _service.GenerateAuthorizationUrlAsync(ProviderId);
        _tokenEndpoint.Respond(IdToken(request.Nonce!));

        await _service.HandleCallbackAsync("the-code", request.State, request.State, providerUser: foreignUser);

        _subjects.Verify(s => s.FindOrCreateFromOidcAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<bool>(), null, It.IsAny<IEnumerable<string>?>()));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""{"name":{"firstName":"Ada","lastName":"Lovelace"},"email":"a@b.c"}""", "Ada Lovelace")]
    [InlineData("""{"name":{"firstName":"Ada"}}""", "Ada")]
    [InlineData("""{"name":{"firstName":"  Ada\n","lastName":"\tLove\u0000lace "}}""", "Ada Love lace")]
    [InlineData("""{"email":"a@b.c"}""", null)]
    [InlineData("""{"name":{"firstName":"","lastName":" "}}""", null)]
    [InlineData("not json", null)]
    [InlineData("""["Ada"]""", null)]
    [InlineData(null, null)]
    public void ReadAppleUserName_TakesOnlyAPrintableName(string? user, string? expected)
    {
        OidcAuthService.ReadAppleUserName(user).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReadAppleUserName_CapsTheLength()
    {
        var user = JsonSerializer.Serialize(new { name = new { firstName = new string('a', 300) } });

        OidcAuthService.ReadAppleUserName(user)!.Length.Should().Be(100);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClientSecret_VerifiesWithThePublicKey_AndLivesMinutes()
    {
        var now = DateTimeOffset.UtcNow;

        var secret = AppleClientSecret.Create(AppleProvider(), now);

        var publicKey = ECDsa.Create(_key.ExportParameters(includePrivateParameters: false));
        var result = new JsonWebTokenHandler().ValidateTokenAsync(secret, new TokenValidationParameters
        {
            ValidIssuer = "TEAM123456",
            ValidAudience = "https://appleid.apple.com",
            IssuerSigningKey = new ECDsaSecurityKey(publicKey),
        }).GetAwaiter().GetResult();

        result.IsValid.Should().BeTrue();
        var token = (JsonWebToken)result.SecurityToken;
        (token.ValidTo - token.IssuedAt).Should().Be(TimeSpan.FromMinutes(5));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("escaped")]
    [InlineData("bare")]
    public void ClientSecret_ReadsAKeyFromAOneLineEnvironmentVariable(string form)
    {
        var pem = _key.ExportPkcs8PrivateKeyPem();
        var provider = AppleProvider();
        provider.ClientSecret = form == "escaped"
            ? pem.Replace("\n", "\\n")
            : Convert.ToBase64String(_key.ExportPkcs8PrivateKey());

        var act = () => AppleClientSecret.Create(provider, DateTimeOffset.UtcNow);

        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClientSecret_RefusesAProviderWithoutAKeyId()
    {
        var provider = AppleProvider();
        provider.Apple!.KeyId = "";

        var act = () => AppleClientSecret.Create(provider, DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClientSecret_RefusesAKeyThatIsNotPem()
    {
        var provider = AppleProvider();
        provider.ClientSecret = "not a key";

        var act = () => AppleClientSecret.Create(provider, DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    private static string AppleUser(string first, string last) =>
        JsonSerializer.Serialize(new { name = new { firstName = first, lastName = last }, email = "x7@privaterelay.appleid.com" });

    /// <summary>An unsigned ID token shaped like Apple's, email_verified as a string.</summary>
    private static string IdToken(string nonce)
    {
        static string Part(object o) => Base64UrlEncoder.Encode(JsonSerializer.Serialize(o));

        var claims = new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = ServicesId,
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["sub"] = "001234.apple-user",
            ["nonce"] = nonce,
            ["email"] = "x7@privaterelay.appleid.com",
            ["email_verified"] = "true",
            ["is_private_email"] = "true",
        };

        return $"{Part(new { alg = "RS256", kid = "k" })}.{Part(claims)}.sig";
    }

    /// <summary>Apple's token endpoint: records the last request and answers with tokens.</summary>
    private sealed class TokenEndpoint : HttpMessageHandler
    {
        private string _body = "{}";

        public Dictionary<string, string>? Form { get; private set; }
        public string? Authorization { get; private set; }

        public void Respond(string idToken, string? refreshToken = null) =>
            _body = JsonSerializer.Serialize(new
            {
                access_token = "apple-access",
                token_type = "Bearer",
                expires_in = 3600,
                id_token = idToken,
                refresh_token = refreshToken,
            });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.ToString() != $"{Issuer}/auth/token")
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            Authorization = request.Headers.Authorization?.ToString();
            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
            Form = form.AllKeys.ToDictionary(k => k!, k => form[k]!);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
