using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nocturne.API.Services.AppBuilds;

/// <summary>
/// The GitHub REST calls of the build App. Every repository call uses an installation token. That token
/// carries only the App's permissions (secrets write, actions write, metadata read), and no call here
/// needs more. Tokens are held in memory only and never logged.
/// </summary>
/// <remarks>
/// Failures surface as <see cref="AppBuildException"/>. <c>owner/name</c> arguments are checked by
/// <see cref="AppBuildService.IsRepositoryFullName"/> before they reach a path.
/// </remarks>
public class GitHubBuildAppClient(
    IHttpClientFactory httpClientFactory,
    IOptions<GitHubBuildAppOptions> options,
    TimeProvider time,
    ILogger<GitHubBuildAppClient> logger)
{
    /// <summary>A cached installation token is replaced this long before GitHub expires it.</summary>
    internal static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    private const int MaxUserInstallationPages = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ConcurrentDictionary<long, InstallationToken> _tokens = new();

    private GitHubBuildAppOptions Options => options.Value;

    /// <summary>
    /// An App JWT (RS256). <c>iat</c> is backdated a minute for clock drift, and <c>exp</c> stays under
    /// GitHub's ten-minute limit.
    /// </summary>
    internal string CreateAppJwt()
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat = now - 60, exp = now + 540, iss = Options.ClientId }));
        var signingInput = $"{header}.{payload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(Options.PrivateKey);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    public async Task<string> GetInstallationTokenAsync(long installationId, CancellationToken ct)
    {
        if (_tokens.TryGetValue(installationId, out var cached) && cached.ExpiresAt - TokenRefreshMargin > time.GetUtcNow())
            return cached.Token;

        using var client = GitHubApi.CreateClient(httpClientFactory, CreateAppJwt());
        using var response = await SendAsync(client, new HttpRequestMessage(HttpMethod.Post,
            $"/app/installations/{installationId}/access_tokens"), ct);
        await EnsureSuccessAsync(response, "installation token", AppBuildException.InstallationGone, ct);

        var minted = await ReadAsync<InstallationTokenResponse>(response, ct);
        _tokens[installationId] = new InstallationToken(minted.Token, minted.ExpiresAt);
        return minted.Token;
    }

    /// <summary>
    /// Exchanges the user's OAuth <paramref name="code"/> and looks for <paramref name="installationId"/>
    /// among the installations that user can reach. The user token is used for these two calls only:
    /// it is revoked before this returns and never leaves this method.
    /// </summary>
    /// <returns>The installation, or null when the user cannot reach it.</returns>
    public async Task<GitHubInstallationAccount?> FindUserInstallationAsync(string code, long installationId, CancellationToken ct)
    {
        var userToken = await ExchangeCodeAsync(code, ct);
        try
        {
            using var client = GitHubApi.CreateClient(httpClientFactory, userToken);
            for (var page = 1; page <= MaxUserInstallationPages; page++)
            {
                using var response = await SendAsync(client, new HttpRequestMessage(HttpMethod.Get,
                    $"/user/installations?per_page=100&page={page}"), ct);
                await EnsureSuccessAsync(response, "user installations", AppBuildException.GitHubRefused, ct);

                var listing = await ReadAsync<UserInstallationsResponse>(response, ct);
                if (listing.Installations.FirstOrDefault(i => i.Id == installationId) is { } match)
                    return new GitHubInstallationAccount(match.Account.Login,
                        string.Equals(match.Account.Type, "Organization", StringComparison.Ordinal));
                if (listing.Installations.Count < 100)
                    return null;
            }

            return null;
        }
        finally
        {
            await RevokeUserTokenAsync(userToken);
        }
    }

    public async Task<IReadOnlyList<GitHubRepository>> ListInstallationRepositoriesAsync(long installationId, CancellationToken ct)
    {
        var listing = await GetAsync<InstallationRepositoriesResponse>(installationId,
            "/installation/repositories?per_page=100", "installation repositories", AppBuildException.InstallationGone, ct);
        return listing.Repositories;
    }

    public Task<GitHubRepository> GetRepositoryAsync(long installationId, string fullName, CancellationToken ct) =>
        GetAsync<GitHubRepository>(installationId, $"/repos/{Escape(fullName)}", "repository", AppBuildException.RepositoryNotFound, ct);

    /// <summary>The names of the repository's Actions secrets. GitHub never returns their values.</summary>
    public async Task<IReadOnlyList<string>> ListSecretNamesAsync(long installationId, string fullName, CancellationToken ct)
    {
        var listing = await GetAsync<SecretsResponse>(installationId,
            $"/repos/{Escape(fullName)}/actions/secrets?per_page=100", "secrets", AppBuildException.RepositoryNotFound, ct);
        return listing.Secrets.Select(secret => secret.Name).ToList();
    }

    public Task<GitHubSecretKey> GetSecretPublicKeyAsync(long installationId, string fullName, CancellationToken ct) =>
        GetAsync<GitHubSecretKey>(installationId, $"/repos/{Escape(fullName)}/actions/secrets/public-key",
            "secret public key", AppBuildException.RepositoryNotFound, ct);

    /// <summary>The value is a libsodium sealed box, base64, made by the browser.</summary>
    public Task PutSecretAsync(long installationId, string fullName, string name, string encryptedValue, string keyId, CancellationToken ct) =>
        SendAsync(installationId, HttpMethod.Put, $"/repos/{Escape(fullName)}/actions/secrets/{Uri.EscapeDataString(name)}",
            new { encrypted_value = encryptedValue, key_id = keyId }, "secret write", ct);

    public Task DispatchWorkflowAsync(long installationId, string fullName, string workflowFile, string gitRef,
        IReadOnlyDictionary<string, string> inputs, CancellationToken ct) =>
        SendAsync(installationId, HttpMethod.Post,
            $"/repos/{Escape(fullName)}/actions/workflows/{Uri.EscapeDataString(workflowFile)}/dispatches",
            new { @ref = gitRef, inputs }, "workflow dispatch", ct);

    public async Task<IReadOnlyList<GitHubWorkflowRun>> ListWorkflowRunsAsync(long installationId, string fullName,
        string workflowFile, int count, CancellationToken ct)
    {
        var listing = await GetAsync<WorkflowRunsResponse>(installationId,
            $"/repos/{Escape(fullName)}/actions/workflows/{Uri.EscapeDataString(workflowFile)}/runs?per_page={count}",
            "workflow runs", AppBuildException.RepositoryNotFound, ct);
        return listing.WorkflowRuns;
    }

    private async Task<T> GetAsync<T>(long installationId, string path, string what, string notFoundCode, CancellationToken ct)
    {
        using var client = GitHubApi.CreateClient(httpClientFactory, await GetInstallationTokenAsync(installationId, ct));
        using var response = await SendAsync(client, new HttpRequestMessage(HttpMethod.Get, path), ct);
        await EnsureInstallationSuccessAsync(response, installationId, what, notFoundCode, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task SendAsync(long installationId, HttpMethod method, string path, object body, string what, CancellationToken ct)
    {
        using var client = GitHubApi.CreateClient(httpClientFactory, await GetInstallationTokenAsync(installationId, ct));
        using var response = await SendAsync(client, new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body),
        }, ct);
        await EnsureInstallationSuccessAsync(response, installationId, what, AppBuildException.RepositoryNotFound, ct);
    }

    private async Task<string> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        using var client = GitHubApi.CreateClient(httpClientFactory, null);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Options.ClientId!,
                ["client_secret"] = Options.ClientSecret!,
                ["code"] = code,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(client, request, ct);
        await EnsureSuccessAsync(response, "code exchange", AppBuildException.GitHubRefused, ct);

        // GitHub answers a bad or spent code with 200 and an "error" member.
        var exchange = await ReadAsync<CodeExchangeResponse>(response, ct);
        if (string.IsNullOrEmpty(exchange.AccessToken))
        {
            logger.LogWarning("GitHub refused the build App's OAuth code: {Error}", exchange.Error ?? "no token");
            throw new AppBuildException(AppBuildException.AuthorizationDenied);
        }

        return exchange.AccessToken;
    }

    private async Task RevokeUserTokenAsync(string userToken)
    {
        try
        {
            using var client = GitHubApi.CreateClient(httpClientFactory, null);
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/applications/{Uri.EscapeDataString(Options.ClientId!)}/token")
            {
                Content = JsonContent.Create(new { access_token = userToken }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Options.ClientId}:{Options.ClientSecret}")));
            using var response = await client.SendAsync(request, CancellationToken.None);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("GitHub did not revoke the build App's user token: {StatusCode}", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("GitHub did not revoke the build App's user token: {Error}", ex.GetType().Name);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            try
            {
                return await client.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw new AppBuildException(AppBuildException.GitHubUnavailable);
            }
        }
    }

    /// <summary>A 401 on an installation token means it was revoked early, so it is dropped from the cache.</summary>
    private Task EnsureInstallationSuccessAsync(HttpResponseMessage response, long installationId, string what, string notFoundCode, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            _tokens.TryRemove(installationId, out _);
        return EnsureSuccessAsync(response, what, notFoundCode, ct);
    }

    private Task EnsureSuccessAsync(HttpResponseMessage response, string what, string notFoundCode, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return Task.CompletedTask;

        var code = Classify(response, notFoundCode);
        logger.LogWarning("GitHub {What} call failed with {StatusCode} ({Code})", what, (int)response.StatusCode, code);
        throw new AppBuildException(code);
    }

    internal static string Classify(HttpResponseMessage response, string notFoundCode) => response.StatusCode switch
    {
        HttpStatusCode.NotFound => notFoundCode,
        HttpStatusCode.TooManyRequests => AppBuildException.GitHubRateLimited,
        HttpStatusCode.Forbidden when response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
            && remaining.FirstOrDefault() == "0" => AppBuildException.GitHubRateLimited,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AppBuildException.GitHubRefused,
        HttpStatusCode.UnprocessableEntity => AppBuildException.GitHubRejected,
        _ => AppBuildException.GitHubUnavailable,
    };

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new AppBuildException(AppBuildException.GitHubUnavailable);
        }
        catch (JsonException)
        {
            throw new AppBuildException(AppBuildException.GitHubUnavailable);
        }
    }

    private static string Escape(string fullName)
    {
        var slash = fullName.IndexOf('/');
        return $"{Uri.EscapeDataString(fullName[..slash])}/{Uri.EscapeDataString(fullName[(slash + 1)..])}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record InstallationToken(string Token, DateTimeOffset ExpiresAt);

    private sealed record InstallationTokenResponse(string Token, DateTimeOffset ExpiresAt);

    private sealed record CodeExchangeResponse(string? AccessToken, string? Error);

    private sealed record UserInstallationsResponse(List<UserInstallation> Installations);

    private sealed record UserInstallation(long Id, InstallationAccount Account);

    private sealed record InstallationAccount(string Login, string? Type);

    private sealed record InstallationRepositoriesResponse(List<GitHubRepository> Repositories);

    private sealed record SecretsResponse(List<SecretName> Secrets);

    private sealed record SecretName(string Name);

    private sealed record WorkflowRunsResponse(List<GitHubWorkflowRun> WorkflowRuns);
}

public sealed record GitHubInstallationAccount(string Login, bool IsOrganization);

public sealed record GitHubRepository(
    string FullName,
    bool Private,
    bool IsTemplate,
    string? DefaultBranch,
    string HtmlUrl,
    GitHubTemplateRepository? TemplateRepository);

public sealed record GitHubTemplateRepository(string FullName);

public sealed record GitHubSecretKey(string KeyId, string Key);

public sealed record GitHubWorkflowRun(
    long Id,
    int RunNumber,
    string? DisplayTitle,
    string? Status,
    string? Conclusion,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string HtmlUrl);
