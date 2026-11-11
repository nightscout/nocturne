using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Nocturne.API.Multitenancy;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// <see cref="DocsSiteCorsPolicy"/> through the real pipeline: no site listed (the default), a
/// listed site against an ordinary tenant and against the demo tenant, and the predicates alone.
/// </summary>
/// <remarks>
/// Every listed host reaches the seeded tenant on its own subdomain of <see cref="BaseDomain"/>:
/// the apex never serves a demo tenant, and the ordinary and demo cases differ only in the flag.
/// </remarks>
public static class DocsSiteCorsTests
{
    private const string DocsSite = "https://getnocturne.dev";
    private const string BaseDomain = "nocturne.test";

    public class ListedFactory : AuthenticationTestFactory
    {
        protected virtual string Features => "careportal";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{DocsSiteCorsPolicy.ConfigKey}:0"] = DocsSite,
                    ["Features:Enable"] = Features,
                    ["BASE_DOMAIN"] = BaseDomain,
                }));
        }

        public HttpClient CreateTenantClient() => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://default.{BaseDomain}"),
        });
    }

    public sealed class ListedWithLegacyCorsFactory : ListedFactory
    {
        protected override string Features => "careportal cors";
    }

    public sealed class ListedDemoFactory : ListedFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(_ =>
            {
                using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
                    .UseSqlite(Connection)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options);
                db.Tenants.ExecuteUpdate(t => t.SetProperty(x => x.IsDemo, true));
            });
        }
    }

    private static HttpRequestMessage Preflight(string path, string origin, string method = "GET")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        return request;
    }

    private static HttpRequestMessage Get(string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", origin);
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    public sealed class Unlisted(AuthenticationTestFactory factory) : IClassFixture<AuthenticationTestFactory>
    {
        [Fact]
        public async Task The_docs_site_gets_no_allow_origin()
        {
            var response = await factory.CreateClient().SendAsync(
                Preflight("/api/v4/Activity", DocsSite), CancellationToken.None);

            Header(response, "Access-Control-Allow-Origin").Should().BeNull();
        }
    }

    public sealed class ListedOnAnOrdinaryTenant(ListedFactory factory) : IClassFixture<ListedFactory>
    {
        [Fact]
        public async Task A_request_is_served_without_allow_origin()
        {
            var response = await factory.CreateTenantClient().SendAsync(
                Get("/api/auth/oidc/session", DocsSite), CancellationToken.None);

            response.IsSuccessStatusCode.Should().BeTrue();
            Header(response, "Access-Control-Allow-Origin").Should().BeNull();
        }

        [Theory]
        [InlineData("/api/v4/Activity")]
        [InlineData("/api/v1/entries.json")]
        public async Task The_docs_site_gets_no_allow_origin(string path)
        {
            var client = factory.CreateTenantClient();

            (await client.SendAsync(Preflight(path, DocsSite), CancellationToken.None))
                .Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
            (await client.SendAsync(Get(path, DocsSite), CancellationToken.None))
                .Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        }
    }

    public sealed class ListedOnAnOrdinaryTenantWithLegacyCors(ListedWithLegacyCorsFactory factory)
        : IClassFixture<ListedWithLegacyCorsFactory>
    {
        [Fact]
        public async Task The_legacy_api_still_answers_any_origin()
        {
            var response = await factory.CreateTenantClient().SendAsync(
                Preflight("/api/v1/entries.json", DocsSite), CancellationToken.None);

            Header(response, "Access-Control-Allow-Origin").Should().Be("*");
        }

        [Fact]
        public async Task The_v4_api_gets_no_allow_origin()
        {
            var response = await factory.CreateTenantClient().SendAsync(
                Preflight("/api/v4/Activity", DocsSite), CancellationToken.None);

            Header(response, "Access-Control-Allow-Origin").Should().BeNull();
        }
    }

    public sealed class ListedOnTheDemo(ListedDemoFactory factory) : IClassFixture<ListedDemoFactory>
    {
        private HttpClient CreateClient() => factory.CreateTenantClient();

        [Theory]
        [InlineData("/api/v4/Activity", "GET")]
        [InlineData("/api/v4/Activity", "POST")]
        [InlineData("/api/v4/demo/session", "GET")]
        [InlineData("/api/v1/entries.json", "GET")]
        public async Task A_preflight_is_answered_without_credentials(string path, string method)
        {
            var response = await CreateClient().SendAsync(
                Preflight(path, DocsSite, method), CancellationToken.None);

            Header(response, "Access-Control-Allow-Origin").Should().Be(DocsSite);
            Header(response, "Access-Control-Allow-Credentials").Should().BeNull();
            Header(response, "Vary").Should().Contain("Origin");
        }

        [Fact]
        public async Task A_request_is_answered_without_credentials()
        {
            var request = Get("/api/auth/oidc/session", DocsSite);
            request.Headers.Add("Cookie", "nocturne-session=anything");

            var response = await CreateClient().SendAsync(request, CancellationToken.None);

            Header(response, "Access-Control-Allow-Origin").Should().Be(DocsSite);
            Header(response, "Access-Control-Allow-Credentials").Should().BeNull();
        }

        [Fact]
        public async Task Another_origin_is_not_admitted()
        {
            var response = await CreateClient().SendAsync(
                Preflight("/api/v4/Activity", "https://evil.example"), CancellationToken.None);

            Header(response, "Access-Control-Allow-Origin").Should().BeNull();
        }

        [Fact]
        public async Task The_documentation_paths_keep_the_any_origin_policy()
        {
            var response = await CreateClient().SendAsync(
                Get("/openapi/nocturne.json", DocsSite), CancellationToken.None);

            Header(response, "Access-Control-Allow-Origin").Should().Be("*");
        }
    }

    public sealed class Predicates
    {
        private static readonly string[] Listed = [DocsSite, "https://app.nocturne.run"];

        private static HttpContext WithOrigin(params string[] origins)
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Origin = origins;
            return context;
        }

        [Fact]
        public void A_listed_origin_is_listed() =>
            DocsSiteCorsPolicy.IsListedOrigin(WithOrigin(DocsSite), Listed, "nocturne.run", false)
                .Should().BeTrue();

        [Fact]
        public void A_listed_base_domain_origin_keeps_the_credentialed_policy() =>
            DocsSiteCorsPolicy.IsListedOrigin(WithOrigin("https://app.nocturne.run"), Listed, "nocturne.run", false)
                .Should().BeFalse();

        [Fact]
        public void Two_origin_headers_are_not_listed() =>
            DocsSiteCorsPolicy.IsListedOrigin(WithOrigin(DocsSite, DocsSite), Listed, "nocturne.run", false)
                .Should().BeFalse();

        [Fact]
        public void Nothing_is_listed_by_default() =>
            DocsSiteCorsPolicy.Origins(new ConfigurationBuilder().Build()).Should().BeEmpty();

        [Fact]
        public void Only_bare_https_origins_are_listed()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{DocsSiteCorsPolicy.ConfigKey}:0"] = " https://getnocturne.dev/ ",
                    [$"{DocsSiteCorsPolicy.ConfigKey}:1"] = "http://getnocturne.dev",
                    [$"{DocsSiteCorsPolicy.ConfigKey}:2"] = "https://getnocturne.dev/docs",
                    [$"{DocsSiteCorsPolicy.ConfigKey}:3"] = "*",
                }).Build();

            DocsSiteCorsPolicy.Origins(configuration).Should().Equal(DocsSite);
        }
    }
}
