using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Services;
using Nocturne.API.Tests.Infrastructure;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// The relay through the real pipeline: tenant resolution, authentication, rate limiting and form
/// binding all sit in front of the controller, and any of them can refuse an anonymous
/// server-to-server POST before the controller's own gate is reached.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SupportRelayPipelineTests(SupportRelayPipelineTests.RelayHostFactory relayHost)
    : IClassFixture<SupportRelayPipelineTests.RelayHostFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    private static MultipartFormDataContent Submission(byte[]? image = null, string imageType = "image/png")
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent("bug"), "template" },
            { new StringContent("[test] Synthetic relayed issue"), "title" },
            { new StringContent("Synthetic description"), "description" },
            { new StringContent("{\"userAgent\":\"test\"}"), "diagnosticInfo" },
        };

        if (image is not null)
        {
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = new MediaTypeHeaderValue(imageType);
            content.Add(file, "images", "shot.png");
        }

        return content;
    }

    [Fact]
    public async Task AnAnonymousRelay_FilesTheIssue_OnARelayHost()
    {
        using var client = relayHost.CreateClient();

        var response = await client.PostAsync("/api/v4/support/relay", Submission(Png));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var sent = relayHost.Sent.Where(s => s.Body.Contains("[test] Synthetic relayed issue")).ToList();
        sent.Should().ContainSingle(s => s.Method == HttpMethod.Post && s.Path == "/repos/o/r/issues")
            .Which.Authorization.Should().Be("Bearer ghp_relay_host");
    }

    [Fact]
    public async Task AnAnonymousRelay_IsStillValidated_OnARelayHost()
    {
        using var client = relayHost.CreateClient();

        var response = await client.PostAsync(
            "/api/v4/support/relay", Submission("<html></html>"u8.ToArray(), "image/png"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheDirectIngress_StillRefusesAnAnonymousCaller_OnARelayHost()
    {
        using var client = relayHost.CreateClient();

        var response = await client.PostAsync("/api/v4/support/issues", Submission());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheRelay_IsNotFound_OnAnInstanceThatDidNotOptIn()
    {
        using var factory = new SupportHostFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v4/support/relay", Submission());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // The controller's refusal rather than tenant resolution's bodiless one, so the gate
        // under test is the one answering.
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    /// <summary>
    /// A default instance. The diagnostics service is stubbed only because the SQLite pipeline
    /// cannot build the repositories behind it.
    /// </summary>
    public class SupportHostFactory : AuthenticationTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddSingleton(Mock.Of<ISupportDiagnosticsService>()));
        }
    }

    /// <summary>
    /// An instance configured the way nocturne.run is, with GitHub answered by a stub.
    /// </summary>
    public sealed class RelayHostFactory : SupportHostFactory
    {
        public ConcurrentQueue<(HttpMethod Method, string Path, string? Authorization, string Body)> Sent { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["GitHub:IssuesPat"] = "ghp_relay_host",
                    ["GitHub:AcceptRelayedIssues"] = "true",
                    ["GitHub:Owner"] = "o",
                    ["GitHub:Repo"] = "r",
                }));

            builder.ConfigureServices(services =>
            {
                var handler = new StubHandler(async request =>
                {
                    var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                    Sent.Enqueue((request.Method, request.RequestUri!.AbsolutePath,
                        request.Headers.Authorization?.ToString(), body));

                    return request.Method == HttpMethod.Put
                        ? Json("""{"content":{"download_url":"https://raw.example/shot.png"}}""")
                        : Json("""{"number":9,"html_url":"https://github.com/o/r/issues/9"}""");
                });
                var httpClients = new Mock<IHttpClientFactory>();
                httpClients.Setup(f => f.CreateClient(It.IsAny<string>()))
                    .Returns(() => new HttpClient(handler, disposeHandler: false));

                services.AddSingleton(sp => new GitHubIssueService(
                    httpClients.Object,
                    sp.GetRequiredService<IOptions<GitHubIssueOptions>>(),
                    NullLogger<GitHubIssueService>.Instance));
            });
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.Created)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
