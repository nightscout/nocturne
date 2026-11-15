using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.AppBuilds;

namespace Nocturne.API.Tests.Services.AppBuilds;

/// <summary>
/// A stub of the GitHub REST API behind <see cref="GitHubBuildAppClient"/>. A route answers with the
/// first matching handler; an unrouted call answers 599 so a test sees it fail.
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public const long InstallationId = 4242;
    public const string Repository = "alice/my-aaps";

    private readonly List<(HttpMethod Method, string PathAndQuery, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<Recorded> Requests { get; } = [];

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));

    public RSA Key { get; } = RSA.Create(2048);

    public GitHubBuildAppOptions Options { get; }

    public FakeGitHub()
    {
        Options = new GitHubBuildAppOptions
        {
            Slug = "nocturne-builds",
            ClientId = "Iv23client",
            ClientSecret = "client-secret",
            PrivateKey = Key.ExportRSAPrivateKeyPem(),
        };
        On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
            _ => Json(HttpStatusCode.Created, new { token = "ghs_installation", expires_at = Time.GetUtcNow().AddHours(1) }));
    }

    public GitHubBuildAppClient CreateClient(Microsoft.Extensions.Logging.ILogger<GitHubBuildAppClient>? logger = null, GitHubBuildAppOptions? options = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(this, disposeHandler: false));
        return new GitHubBuildAppClient(factory.Object, Microsoft.Extensions.Options.Options.Create(options ?? Options), Time,
            logger ?? NullLogger<GitHubBuildAppClient>.Instance);
    }

    /// <summary>Replaces any route for the same method and path.</summary>
    public FakeGitHub On(HttpMethod method, string pathAndQuery, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.RemoveAll(route => route.Method == method && route.PathAndQuery == pathAndQuery);
        _routes.Add((method, pathAndQuery, respond));
        return this;
    }

    public FakeGitHub OnRepository(bool isPrivate = true, bool isTemplate = false, string? template = "nightscout/aaps-builder") =>
        On(HttpMethod.Get, $"/repos/{Repository}", _ => Json(HttpStatusCode.OK, new
        {
            full_name = Repository,
            @private = isPrivate,
            is_template = isTemplate,
            default_branch = "main",
            html_url = $"https://github.com/{Repository}",
            template_repository = template is null ? null : new { full_name = template },
        }));

    public FakeGitHub OnUserFlow(params long[] reachableInstallations) =>
        On(HttpMethod.Post, "https://github.com/login/oauth/access_token", _ => Json(HttpStatusCode.OK, new { access_token = "ghu_user", token_type = "bearer" }))
            .On(HttpMethod.Get, "/user/installations?per_page=100&page=1", _ => Json(HttpStatusCode.OK, new
            {
                total_count = reachableInstallations.Length,
                installations = reachableInstallations.Select(id => new { id, account = new { login = "alice", type = "User" } }),
            }))
            .On(HttpMethod.Delete, "/applications/Iv23client/token", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
            .OnUserRepositories((Repository, true));

    /// <summary>The installation's repositories as the authorizing user sees them, with whether they administer each.</summary>
    public FakeGitHub OnUserRepositories(params (string FullName, bool Admin)[] repositories) =>
        On(HttpMethod.Get, $"/user/installations/{InstallationId}/repositories?per_page=100&page=1", _ => Json(HttpStatusCode.OK, new
        {
            total_count = repositories.Length,
            repositories = repositories.Select(r => new
            {
                full_name = r.FullName,
                @private = true,
                is_template = false,
                html_url = $"https://github.com/{r.FullName}",
                permissions = new { admin = r.Admin, push = true, pull = true },
            }),
        }));

    public FakeGitHub OnInstallationRepositories(params string[] fullNames) =>
        On(HttpMethod.Get, "/installation/repositories?per_page=100&page=1", _ => Json(HttpStatusCode.OK, new
        {
            total_count = fullNames.Length,
            repositories = fullNames.Select(name => new { full_name = name, @private = true, is_template = false, html_url = $"https://github.com/{name}" }),
        }));

    public FakeGitHub OnSecrets(params string[] names) =>
        On(HttpMethod.Get, $"/repos/{Repository}/actions/secrets?per_page=100", _ => Json(HttpStatusCode.OK, new
        {
            total_count = names.Length,
            secrets = names.Select(name => new { name, created_at = "2026-11-01T00:00:00Z", updated_at = "2026-11-01T00:00:00Z" }),
        }));

    public static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new Recorded(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));

        var key = request.RequestUri!.Host == "api.github.com" ? request.RequestUri.PathAndQuery : request.RequestUri.ToString();
        var route = _routes.FirstOrDefault(r => r.Method == request.Method && r.PathAndQuery == key);
        return route.Respond is null ? new HttpResponseMessage((HttpStatusCode)599) : route.Respond(request);
    }

    public sealed record Recorded(HttpMethod Method, Uri Uri, string? Authorization, string? Body)
    {
        public string Path => Uri.Host == "api.github.com" ? Uri.PathAndQuery : Uri.ToString();
    }
}
