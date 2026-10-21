using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Nocturne.API.Multitenancy;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// <see cref="LegacyApiCorsPolicy"/> through the real pipeline, with the instance's enable list off
/// (the default) and on, and the branch predicate on its own.
/// </summary>
/// <remarks>
/// The test host cannot give Program a usable base domain: appsettings.example.json is layered over
/// host settings and sets a single-label one, so no origin is a base-domain origin in the pipeline
/// tests. <see cref="Branch"/> covers the base-domain rule instead.
/// </remarks>
public static class LegacyApiCorsTests
{
    private const string ForeignOrigin = "https://reporter.example";

    public sealed class CorsEnabledFactory : AuthenticationTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Features:Enable"] = "careportal basal cors" }));
        }
    }

    private static HttpRequestMessage Preflight(string path, string requestHeaders)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", ForeignOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", requestHeaders);
        return request;
    }

    private static HttpRequestMessage Get(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", ForeignOrigin);
        return request;
    }

    private static string? AllowOrigin(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;

    private static IEnumerable<string> HeaderList(HttpResponseMessage response, string name) =>
        response.Headers.GetValues(name).SelectMany(v => v.Split(',')).Select(v => v.Trim());

    public sealed class Off(AuthenticationTestFactory factory) : IClassFixture<AuthenticationTestFactory>
    {
        [Fact]
        public async Task A_foreign_preflight_to_the_legacy_api_gets_no_allow_origin()
        {
            var response = await factory.CreateClient().SendAsync(
                Preflight("/api/v1/entries.json", "api-secret"), CancellationToken.None);

            AllowOrigin(response).Should().BeNull();
        }

        [Fact]
        public async Task A_foreign_get_to_the_legacy_api_gets_no_allow_origin()
        {
            var response = await factory.CreateClient().SendAsync(
                Get("/api/v1/entries.json?token=x"), CancellationToken.None);

            AllowOrigin(response).Should().BeNull();
        }
    }

    public sealed class On(CorsEnabledFactory factory) : IClassFixture<CorsEnabledFactory>
    {
        [Theory]
        [InlineData("/api/v1/entries.json")]
        [InlineData("/api/v2/properties")]
        [InlineData("/api/v3/entries")]
        [InlineData("/api/versions")]
        [InlineData("/pebble")]
        public async Task A_foreign_preflight_is_answered_for_any_origin_without_credentials(string path)
        {
            var response = await factory.CreateClient().SendAsync(
                Preflight(path, "api-secret,authorization,content-type"), CancellationToken.None);

            response.IsSuccessStatusCode.Should().BeTrue();
            AllowOrigin(response).Should().Be("*");
            HeaderList(response, "Access-Control-Allow-Headers").Should().BeEquivalentTo(
                "Content-Type", "Authorization", "Content-Length", "X-Requested-With", "api-secret");
            HeaderList(response, "Access-Control-Allow-Methods").Should().BeEquivalentTo(
                "GET", "PUT", "POST", "DELETE", "OPTIONS");
            response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
        }

        [Fact]
        public async Task A_preflight_for_a_header_outside_the_list_is_not_granted_it()
        {
            var response = await factory.CreateClient().SendAsync(
                Preflight("/api/v1/entries.json", "x-custom"), CancellationToken.None);

            HeaderList(response, "Access-Control-Allow-Headers").Should().NotContain("x-custom");
        }

        [Fact]
        public async Task A_token_get_carries_the_any_origin_header()
        {
            var response = await factory.CreateClient().SendAsync(
                Get("/api/v1/entries.json?token=x"), CancellationToken.None);

            AllowOrigin(response).Should().Be("*");
            response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
        }

        [Fact]
        public async Task A_foreign_origin_reads_legacy_data_with_the_api_secret_header()
        {
            var request = Get("/api/v1/entries.json");
            request.Headers.Add("api-secret", TestDatabaseSeeder.Sha1Hex(AuthenticationTestFactory.ApiSecret));

            var response = await factory.CreateClient().SendAsync(request, CancellationToken.None);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            AllowOrigin(response).Should().Be("*");
        }

        [Theory]
        [InlineData("/api/v4/entries")]
        [InlineData("/api/auth/passkey/options")]
        [InlineData("/api/oauth/token")]
        [InlineData("/api/v10/entries")]
        public async Task Routes_outside_the_legacy_api_refuse_a_foreign_origin(string path)
        {
            var response = await factory.CreateClient().SendAsync(
                Preflight(path, "content-type"), CancellationToken.None);

            AllowOrigin(response).Should().BeNull();
        }
    }

    public sealed class Branch
    {
        private const string BaseDomain = "nocturne.test";

        private static readonly IConfiguration Enabled = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Enable"] = "careportal cors" })
            .Build();

        private static bool Applies(string path, string origin, IConfiguration configuration)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Request.Headers.Origin = origin;
            return LegacyApiCorsPolicy.Applies(context, configuration, BaseDomain, allowLocalhost: false);
        }

        [Theory]
        [InlineData("https://nocturne.test")]
        [InlineData("https://default.nocturne.test")]
        [InlineData("https://abc.share.nocturne.test")]
        public void A_base_domain_origin_stays_on_the_credentialed_default(string origin) =>
            Applies("/api/v1/entries.json", origin, Enabled).Should().BeFalse();

        [Fact]
        public void A_foreign_origin_on_the_legacy_api_takes_the_branch() =>
            Applies("/api/v3/entries", ForeignOrigin, Enabled).Should().BeTrue();

        [Fact]
        public void The_branch_is_off_unless_the_enable_list_names_cors() =>
            Applies("/api/v1/entries.json", ForeignOrigin, new ConfigurationBuilder().Build())
                .Should().BeFalse();
    }
}
