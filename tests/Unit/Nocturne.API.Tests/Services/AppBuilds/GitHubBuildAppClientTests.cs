using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nocturne.API.Services.AppBuilds;

namespace Nocturne.API.Tests.Services.AppBuilds;

[Trait("Category", "Unit")]
public class GitHubBuildAppClientTests
{
    private readonly FakeGitHub _github = new();

    [Fact]
    public void The_app_jwt_is_an_RS256_token_issued_by_the_client_id_and_signed_with_the_app_key()
    {
        var jwt = _github.CreateClient().CreateAppJwt();

        var parts = jwt.Split('.');
        parts.Should().HaveCount(3);
        var header = JsonDocument.Parse(FromBase64Url(parts[0])).RootElement;
        header.GetProperty("alg").GetString().Should().Be("RS256");
        header.GetProperty("typ").GetString().Should().Be("JWT");

        var payload = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement;
        var now = _github.Time.GetUtcNow().ToUnixTimeSeconds();
        payload.GetProperty("iss").GetString().Should().Be("Iv23client");
        payload.GetProperty("iat").GetInt64().Should().Be(now - 60);
        payload.GetProperty("exp").GetInt64().Should().BeLessThanOrEqualTo(now + 600, "GitHub refuses an App JWT that lives longer than ten minutes");

        _github.Key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Should().BeTrue();
    }

    [Fact]
    public async Task An_installation_token_is_minted_with_the_app_jwt_and_reused_until_shortly_before_it_expires()
    {
        var client = _github.CreateClient();

        (await client.GetInstallationTokenAsync(FakeGitHub.InstallationId, default)).Should().Be("ghs_installation");
        _github.Time.Advance(TimeSpan.FromMinutes(50));
        await client.GetInstallationTokenAsync(FakeGitHub.InstallationId, default);

        var mints = _github.Requests.Where(r => r.Path.EndsWith("/access_tokens", StringComparison.Ordinal)).ToList();
        mints.Should().ContainSingle();
        mints[0].Method.Should().Be(HttpMethod.Post);
        mints[0].Authorization.Should().StartWith("Bearer ey", "the token is requested with the App JWT");

        _github.Time.Advance(TimeSpan.FromMinutes(6));
        await client.GetInstallationTokenAsync(FakeGitHub.InstallationId, default);
        _github.Requests.Count(r => r.Path.EndsWith("/access_tokens", StringComparison.Ordinal)).Should().Be(2,
            $"a token within {GitHubBuildAppClient.TokenRefreshMargin} of expiry is replaced");
    }

    [Fact]
    public async Task A_revoked_installation_token_is_dropped_from_the_cache()
    {
        var client = _github.CreateClient();
        _github.On(HttpMethod.Get, $"/repos/{FakeGitHub.Repository}", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await FluentActions.Awaiting(() => client.GetRepositoryAsync(FakeGitHub.InstallationId, FakeGitHub.Repository, default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.GitHubRefused);
        _github.OnRepository();
        await client.GetRepositoryAsync(FakeGitHub.InstallationId, FakeGitHub.Repository, default);

        _github.Requests.Count(r => r.Path.EndsWith("/access_tokens", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task Repository_calls_use_the_installation_token_and_the_documented_routes()
    {
        var client = _github.CreateClient();
        _github.OnRepository().OnSecrets("KEYSTORE_SET")
            .On(HttpMethod.Get, $"/repos/{FakeGitHub.Repository}/actions/secrets/public-key",
                _ => FakeGitHub.Json(HttpStatusCode.OK, new { key_id = "568250167242549743", key = "aGVsbG8=" }))
            .On(HttpMethod.Put, $"/repos/{FakeGitHub.Repository}/actions/secrets/KEYSTORE_SET",
                _ => new HttpResponseMessage(HttpStatusCode.Created))
            .On(HttpMethod.Post, $"/repos/{FakeGitHub.Repository}/actions/workflows/build.yml/dispatches",
                _ => new HttpResponseMessage(HttpStatusCode.NoContent))
            .On(HttpMethod.Get, $"/repos/{FakeGitHub.Repository}/actions/workflows/build.yml/runs?per_page=5",
                _ => FakeGitHub.Json(HttpStatusCode.OK, new
                {
                    total_count = 1,
                    workflow_runs = new[]
                    {
                        new
                        {
                            id = 7L, run_number = 3, display_title = "Build AAPS latest (fullRelease)", status = "completed",
                            conclusion = "success", created_at = "2026-11-16T08:00:00Z", updated_at = "2026-11-16T08:20:00Z",
                            html_url = "https://github.com/alice/my-aaps/actions/runs/7",
                        },
                    },
                }));

        var repository = await client.GetRepositoryAsync(FakeGitHub.InstallationId, FakeGitHub.Repository, default);
        var secrets = await client.ListSecretNamesAsync(FakeGitHub.InstallationId, FakeGitHub.Repository, default);
        var key = await client.GetSecretPublicKeyAsync(FakeGitHub.InstallationId, FakeGitHub.Repository, default);
        await client.PutSecretAsync(FakeGitHub.InstallationId, FakeGitHub.Repository, "KEYSTORE_SET", "c2VhbGVk", key.KeyId, default);
        await client.DispatchWorkflowAsync(FakeGitHub.InstallationId, FakeGitHub.Repository, "build.yml", "main",
            new Dictionary<string, string> { ["version"] = "latest", ["variant"] = "fullRelease" }, default);
        var runs = await client.ListWorkflowRunsAsync(FakeGitHub.InstallationId, FakeGitHub.Repository, "build.yml", 5, default);

        repository.Should().Be(new GitHubRepository(FakeGitHub.Repository, true, false, "main",
            "https://github.com/alice/my-aaps", new GitHubTemplateRepository("nightscout/aaps-builder")));
        secrets.Should().Equal("KEYSTORE_SET");
        key.Should().Be(new GitHubSecretKey("568250167242549743", "aGVsbG8="));
        runs.Should().ContainSingle().Which.DisplayTitle.Should().Be("Build AAPS latest (fullRelease)");

        _github.Requests.Where(r => !r.Path.EndsWith("/access_tokens", StringComparison.Ordinal))
            .Should().OnlyContain(r => r.Authorization == "Bearer ghs_installation");

        var put = _github.Requests.Single(r => r.Method == HttpMethod.Put);
        var putBody = JsonDocument.Parse(put.Body!).RootElement;
        putBody.GetProperty("encrypted_value").GetString().Should().Be("c2VhbGVk");
        putBody.GetProperty("key_id").GetString().Should().Be("568250167242549743");
        putBody.EnumerateObject().Should().HaveCount(2);

        var dispatch = JsonDocument.Parse(_github.Requests.Single(r => r.Path.EndsWith("/dispatches", StringComparison.Ordinal)).Body!).RootElement;
        dispatch.GetProperty("ref").GetString().Should().Be("main");
        dispatch.GetProperty("inputs").GetProperty("variant").GetString().Should().Be("fullRelease");
        dispatch.GetProperty("inputs").GetProperty("version").GetString().Should().Be("latest");
    }

    [Fact]
    public async Task The_user_token_finds_the_installation_and_the_repositories_the_user_administers_and_is_then_revoked()
    {
        var client = _github.CreateClient();
        _github.OnUserFlow(FakeGitHub.InstallationId).OnUserRepositories((FakeGitHub.Repository, true), ("bob/shared", false));

        var installation = (await client.AuthorizeUserInstallationAsync("oauth-code", FakeGitHub.InstallationId, default))!;

        installation.Login.Should().Be("alice");
        installation.IsOrganization.Should().BeFalse();
        installation.AdminRepositories.Should().Equal(FakeGitHub.Repository);
        installation.UserLogin.Should().Be(FakeGitHub.LinkerLogin);
        installation.UserId.Should().Be(FakeGitHub.LinkerId);
        var exchange = _github.Requests.Single(r => r.Uri.Host == "github.com");
        exchange.Body.Should().Contain("code=oauth-code").And.Contain("client_id=Iv23client");
        _github.Requests.Where(r => r.Path.StartsWith("/user/installations", StringComparison.Ordinal))
            .Should().HaveCount(2).And.OnlyContain(r => r.Authorization == "Bearer ghu_user");
        _github.Requests.Single(r => r.Path == "/user").Authorization.Should().Be("Bearer ghu_user");
        var revoke = _github.Requests.Single(r => r.Method == HttpMethod.Delete);
        revoke.Authorization.Should().StartWith("Basic ");
        JsonDocument.Parse(revoke.Body!).RootElement.GetProperty("access_token").GetString().Should().Be("ghu_user");
    }

    [Fact]
    public async Task An_installation_the_user_cannot_reach_is_not_found_and_the_user_token_is_still_revoked()
    {
        var client = _github.CreateClient();
        _github.OnUserFlow(1, 2);

        (await client.AuthorizeUserInstallationAsync("oauth-code", FakeGitHub.InstallationId, default)).Should().BeNull();
        _github.Requests.Should().Contain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task A_refused_code_is_authorization_denied()
    {
        var client = _github.CreateClient();
        _github.On(HttpMethod.Post, "https://github.com/login/oauth/access_token",
            _ => FakeGitHub.Json(HttpStatusCode.OK, new { error = "bad_verification_code" }));

        await FluentActions.Awaiting(() => client.AuthorizeUserInstallationAsync("spent", FakeGitHub.InstallationId, default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.AuthorizationDenied);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, null, AppBuildException.RepositoryNotFound)]
    [InlineData(HttpStatusCode.Unauthorized, null, AppBuildException.GitHubRefused)]
    [InlineData(HttpStatusCode.Forbidden, null, AppBuildException.GitHubRefused)]
    [InlineData(HttpStatusCode.Forbidden, "0", AppBuildException.GitHubRateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, null, AppBuildException.GitHubRateLimited)]
    [InlineData(HttpStatusCode.UnprocessableEntity, null, AppBuildException.GitHubRejected)]
    [InlineData(HttpStatusCode.BadGateway, null, AppBuildException.GitHubUnavailable)]
    public async Task GitHub_failures_map_to_codes(HttpStatusCode status, string? rateLimitRemaining, string expected)
    {
        var client = _github.CreateClient();
        _github.On(HttpMethod.Get, $"/repos/{FakeGitHub.Repository}", _ =>
        {
            var response = new HttpResponseMessage(status);
            if (rateLimitRemaining is not null) response.Headers.Add("x-ratelimit-remaining", rateLimitRemaining);
            return response;
        });

        await FluentActions.Awaiting(() => client.GetRepositoryAsync(FakeGitHub.InstallationId, FakeGitHub.Repository, default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(expected);
    }

    [Fact]
    public async Task An_uninstalled_app_is_installation_gone()
    {
        var client = _github.CreateClient();
        _github.On(HttpMethod.Post, $"/app/installations/{FakeGitHub.InstallationId}/access_tokens",
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await FluentActions.Awaiting(() => client.ListInstallationRepositoriesAsync(FakeGitHub.InstallationId, default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.InstallationGone);
    }

    [Theory]
    [InlineData("admin", FakeGitHub.LinkerId, true)]
    [InlineData("write", FakeGitHub.LinkerId, false)]
    [InlineData("admin", 7, false)]
    public async Task The_linkers_role_is_read_from_the_collaborator_permission_endpoint(string permission, long userId, bool expected)
    {
        _github.OnLinkerPermission(permission, userId);

        (await _github.CreateClient().IsAdministratorAsync(FakeGitHub.InstallationId, FakeGitHub.Repository,
            FakeGitHub.LinkerLogin, FakeGitHub.LinkerId, default)).Should().Be(expected);
        _github.Requests.Single(r => r.Path.EndsWith("/permission", StringComparison.Ordinal))
            .Authorization.Should().Be("Bearer ghs_installation");
    }

    [Fact]
    public async Task A_linker_GitHub_no_longer_knows_is_not_an_administrator()
    {
        _github.On(HttpMethod.Get, $"/repos/{FakeGitHub.Repository}/collaborators/{FakeGitHub.LinkerLogin}/permission",
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await _github.CreateClient().IsAdministratorAsync(FakeGitHub.InstallationId, FakeGitHub.Repository,
            FakeGitHub.LinkerLogin, FakeGitHub.LinkerId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Installation_repositories_are_read_page_by_page()
    {
        var client = _github.CreateClient();
        var firstPage = Enumerable.Range(0, 100).Select(i => $"alice/repo-{i}").ToArray();
        _github.OnInstallationRepositories(firstPage)
            .On(HttpMethod.Get, "/installation/repositories?per_page=100&page=2", _ => FakeGitHub.Json(HttpStatusCode.OK, new
            {
                total_count = 101,
                repositories = new[] { new { full_name = FakeGitHub.Repository, @private = true, is_template = false, html_url = "x" } },
            }));

        var repositories = await client.ListInstallationRepositoriesAsync(FakeGitHub.InstallationId, default);

        repositories.Should().HaveCount(101);
        repositories[^1].FullName.Should().Be(FakeGitHub.Repository);
        _github.Requests.Should().NotContain(r => r.Path.Contains("page=3"), "a short page is the last one");
    }

    [Theory]
    [InlineData("alice/../app")]
    [InlineData("alice/..")]
    [InlineData("alice")]
    [InlineData("alice/my aaps")]
    [InlineData("-alice/my-aaps")]
    public async Task A_name_that_is_not_an_owner_and_name_never_reaches_a_path(string fullName)
    {
        GitHubBuildAppClient.IsRepositoryFullName(fullName).Should().BeFalse();

        await FluentActions.Awaiting(() => _github.CreateClient().GetRepositoryAsync(FakeGitHub.InstallationId, fullName, default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.RepositoryNotFound);
        _github.Requests.Should().BeEmpty();
    }

    [Fact]
    public void A_private_key_given_as_the_base64_of_the_pem_file_is_accepted()
    {
        var options = new GitHubBuildAppOptions
        {
            Slug = _github.Options.Slug,
            ClientId = _github.Options.ClientId,
            ClientSecret = _github.Options.ClientSecret,
            PrivateKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(_github.Options.PrivateKey!)),
        };
        var client = _github.CreateClient(options: options);

        client.IsConfigured.Should().BeTrue();
        var parts = client.CreateAppJwt().Split('.');
        _github.Key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Should().BeTrue();
    }

    [Theory]
    [InlineData("not a key")]
    [InlineData("bm90IGEga2V5")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nAAAA\n-----END RSA PRIVATE KEY-----")]
    public void A_private_key_that_does_not_parse_leaves_the_client_unconfigured(string privateKey)
    {
        var options = new GitHubBuildAppOptions
        {
            Slug = "s", ClientId = "c", ClientSecret = "x", PrivateKey = privateKey,
        };

        _github.CreateClient(options: options).IsConfigured.Should().BeFalse();
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
