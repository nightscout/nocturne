using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Nocturne.API.Multitenancy;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// <see cref="DocsSiteCorsPolicy"/> through the real pipeline, with no site listed (the default)
/// and with one, and the origin list read from configuration on its own.
/// </summary>
public static class DocsSiteCorsTests
{
    private const string DocsSite = "https://getnocturne.dev";

    public sealed class ListedFactory : AuthenticationTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{DocsSiteCorsPolicy.ConfigKey}:0"] = DocsSite,
                    ["Features:Enable"] = "cors",
                }));
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

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    public sealed class Unlisted(AuthenticationTestFactory factory) : IClassFixture<AuthenticationTestFactory>
    {
        [Fact]
        public async Task The_docs_site_gets_no_allow_origin_on_the_api()
        {
            var response = await factory.CreateClient().SendAsync(
                Preflight("/api/v4/Activity", DocsSite), CancellationToken.None);

            Header(response, "Access-Control-Allow-Origin").Should().BeNull();
        }
    }

    public sealed class Listed(ListedFactory factory) : IClassFixture<ListedFactory>
    {
        [Theory]
        [InlineData("/api/v4/Activity", "GET")]
        [InlineData("/api/v4/Activity", "POST")]
        [InlineData("/api/v4/demo/session", "GET")]
        // Ahead of the legacy branch, which the instance also enables here: one CORS middleware
        // answers, so the response carries one Access-Control-Allow-Origin.
        [InlineData("/api/v1/entries.json", "GET")]
        public async Task The_docs_site_may_call_the_api_without_credentials(string path, string method)
        {
            var response = await factory.CreateClient().SendAsync(
                Preflight(path, DocsSite, method), CancellationToken.None);

            Header(response, "Access-Control-Allow-Origin").Should().Be(DocsSite);
            Header(response, "Access-Control-Allow-Credentials").Should().BeNull();
        }

        [Fact]
        public async Task Another_origin_is_not_admitted_to_the_v4_api()
        {
            var response = await factory.CreateClient().SendAsync(
                Preflight("/api/v4/Activity", "https://evil.example"), CancellationToken.None);

            Header(response, "Access-Control-Allow-Origin").Should().BeNull();
        }

        [Fact]
        public async Task The_documentation_paths_keep_the_any_origin_policy()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/openapi/nocturne.json");
            request.Headers.Add("Origin", DocsSite);

            var response = await factory.CreateClient().SendAsync(request, CancellationToken.None);

            Header(response, "Access-Control-Allow-Origin").Should().Be("*");
        }
    }

    public sealed class Origins
    {
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

            DocsSiteCorsPolicy.Origins(configuration).Should().Equal("https://getnocturne.dev");
        }

        [Fact]
        public void Nothing_is_listed_by_default()
        {
            DocsSiteCorsPolicy.Origins(new ConfigurationBuilder().Build()).Should().BeEmpty();
        }
    }
}
