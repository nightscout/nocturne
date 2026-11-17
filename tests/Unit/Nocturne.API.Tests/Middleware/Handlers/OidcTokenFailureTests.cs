using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Middleware.Handlers;

/// <summary>
/// An identity provider or database that cannot be reached says nothing about the token presented, so
/// it must not reach the client as a 401. A token the provider's keys do not vouch for still does.
/// </summary>
[Trait("Category", "Unit")]
public class OidcTokenFailureTests
{
    private sealed class StubHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class ThrowingHttp(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(failure);
    }

    public static TheoryData<Exception> InfrastructureFailures => new()
    {
        new NpgsqlException("connection refused"),
        new RetryLimitExceededException("retries exhausted"),
        new OperationCanceledException(),
    };

    private static string TokenFrom(string issuer)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("oidc-token-failure-test-signing-key-32+chars"));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: issuer,
            audience: "client",
            claims: [new System.Security.Claims.Claim("sub", "external-subject")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
    }

    private static (OidcTokenHandler Handler, DefaultHttpContext Context) Build(
        string issuer, Func<Task<OidcProvider?>> lookup, HttpMessageHandler idp)
    {
        var providers = new Mock<IOidcProviderService>();
        providers.Setup(p => p.GetProviderByIssuerAsync(issuer)).Returns(lookup);

        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient("OidcProvider")).Returns(() => new HttpClient(idp, disposeHandler: false));

        var services = new ServiceCollection().AddSingleton(providers.Object).BuildServiceProvider();

        var handler = new OidcTokenHandler(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OidcTokenHandler>.Instance,
            new ConfigurationBuilder().Build(),
            http.Object,
            Options.Create(new JwtOptions { Issuer = "nocturne" }));

        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {TokenFrom(issuer)}";
        return (handler, context);
    }

    private static OidcProvider ProviderFor(string issuer) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "Test",
        IssuerUrl = issuer,
        ClientId = "client",
        IsEnabled = true,
    };

    [Theory]
    [MemberData(nameof(InfrastructureFailures))]
    public async Task A_provider_lookup_that_fails_propagates(Exception failure)
    {
        const string issuer = "https://lookup-failure.idp.test";
        var (handler, context) = Build(issuer, () => Task.FromException<OidcProvider?>(failure), new ThrowingHttp(failure));

        var authenticate = () => handler.AuthenticateAsync(context);

        (await authenticate.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task A_provider_that_cannot_be_reached_propagates_instead_of_rejecting_the_token()
    {
        const string issuer = "https://outage.idp.test";
        var (handler, context) = Build(
            issuer,
            () => Task.FromResult<OidcProvider?>(ProviderFor(issuer)),
            new ThrowingHttp(new HttpRequestException("name resolution failed")));

        var authenticate = () => handler.AuthenticateAsync(context);

        await authenticate.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task A_token_the_providers_keys_do_not_vouch_for_is_not_authenticated()
    {
        const string issuer = "https://empty-keys.idp.test";
        var idp = new StubHttp(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal)
                    ? "{\"issuer\":\"" + issuer + "\",\"jwks_uri\":\"" + issuer + "/jwks\"}"
                    : "{\"keys\":[]}",
                Encoding.UTF8,
                "application/json"),
        });
        var (handler, context) = Build(issuer, () => Task.FromResult<OidcProvider?>(ProviderFor(issuer)), idp);

        var result = await handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
        result.ShouldSkip.Should().BeFalse();
    }
}
