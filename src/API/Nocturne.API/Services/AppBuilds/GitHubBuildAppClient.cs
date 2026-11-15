using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Nocturne.API.Services.AppBuilds;

/// <summary>
/// The GitHub REST calls of the build App. Every repository call uses an installation token. That token
/// carries only the App's permissions (secrets write, actions write, metadata read), and no call here
/// needs more. Tokens are held in memory only and never logged.
/// </summary>
/// <remarks>
/// Failures surface as <see cref="AppBuildException"/>. An <c>owner/name</c> that
/// <see cref="IsRepositoryFullName"/> rejects never reaches a path.
/// </remarks>
public partial class GitHubBuildAppClient(
    IHttpClientFactory httpClientFactory,
    IOptions<GitHubBuildAppOptions> options,
    TimeProvider time,
    ILogger<GitHubBuildAppClient> logger)
{
    /// <summary>A cached installation token is replaced this long before GitHub expires it.</summary>
    internal static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    private const int PageSize = 100;
    private const int MaxPages = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ConcurrentDictionary<long, InstallationToken> _tokens = new();

    private readonly Lazy<string?> _privateKeyPem = new(() => ReadPrivateKey(options.Value, logger));

    private GitHubBuildAppOptions Options => options.Value;

    /// <summary>
    /// Whether every <c>GitHub:BuildApp</c> key is set and the private key parses. A key that does not
    /// parse is logged once, without its contents, and leaves app builds off.
    /// </summary>
    public bool IsConfigured => Options.IsConfigured && _privateKeyPem.Value is not null;

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
        rsa.ImportFromPem(_privateKeyPem.Value ?? throw new AppBuildException(AppBuildException.NotConfigured));
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
    /// Exchanges the user's OAuth <paramref name="code"/>, finds <paramref name="installationId"/> among
    /// the installations that user can reach, and lists the installation's repositories the user
    /// administers. Seeing an installation proves no control over its repositories: a read-only
    /// collaborator sees it too. The user token is used for these calls only. It is revoked before this
    /// returns and never leaves this method.
    /// </summary>
    /// <returns>The installation, or null when the user cannot reach it.</returns>
    public async Task<GitHubUserInstallation?> AuthorizeUserInstallationAsync(string code, long installationId, CancellationToken ct)
    {
        var userToken = await ExchangeCodeAsync(code, ct);
        try
        {
            using var client = GitHubApi.CreateClient(httpClientFactory, userToken);
            var installations = await ReadPagesAsync<UserInstallationsResponse, UserInstallation>(client, null,
                "/user/installations", "user installations", AppBuildException.GitHubRefused, page => page.Installations, ct);
            if (installations.FirstOrDefault(i => i.Id == installationId) is not { } match)
                return null;

            var repositories = await ReadPagesAsync<InstallationRepositoriesResponse, GitHubRepository>(client, null,
                $"/user/installations/{installationId}/repositories", "user installation repositories",
                AppBuildException.InstallationNotYours, page => page.Repositories, ct);

            using var userResponse = await SendAsync(client, new HttpRequestMessage(HttpMethod.Get, "/user"), ct);
            await EnsureSuccessAsync(userResponse, "user", AppBuildException.GitHubRefused, ct);
            var user = await ReadAsync<GitHubUser>(userResponse, ct);

            return new GitHubUserInstallation(
                match.Account.Login,
                string.Equals(match.Account.Type, "Organization", StringComparison.Ordinal),
                repositories.Where(r => r.Permissions?.Admin == true && IsRepositoryFullName(r.FullName))
                    .Select(r => r.FullName)
                    .ToList(),
                user.Login,
                user.Id);
        }
        finally
        {
            await RevokeUserTokenAsync(userToken);
        }
    }

    public async Task<IReadOnlyList<GitHubRepository>> ListInstallationRepositoriesAsync(long installationId, CancellationToken ct)
    {
        using var client = GitHubApi.CreateClient(httpClientFactory, await GetInstallationTokenAsync(installationId, ct));
        return await ReadPagesAsync<InstallationRepositoriesResponse, GitHubRepository>(client, installationId,
            "/installation/repositories", "installation repositories", AppBuildException.InstallationGone,
            page => page.Repositories, ct);
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

    /// <summary>
    /// Whether the GitHub user <paramref name="userId"/>, last known as <paramref name="userLogin"/>, has the
    /// admin role on the repository now. Needs only the Metadata permission. A renamed or removed user
    /// reads as false: the login no longer names the same id.
    /// </summary>
    public async Task<bool> IsAdministratorAsync(long installationId, string fullName, string userLogin, long userId, CancellationToken ct)
    {
        if (!LoginPattern().IsMatch(userLogin))
            return false;

        var path = $"/repos/{Escape(fullName)}/collaborators/{Uri.EscapeDataString(userLogin)}/permission";
        using var client = GitHubApi.CreateClient(httpClientFactory, await GetInstallationTokenAsync(installationId, ct));
        using var response = await SendAsync(client, new HttpRequestMessage(HttpMethod.Get, path), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureInstallationSuccessAsync(response, installationId, "collaborator permission", AppBuildException.RepositoryNotFound, ct);

        var permission = await ReadAsync<CollaboratorPermissionResponse>(response, ct);
        return permission.User?.Id == userId && string.Equals(permission.Permission, "admin", StringComparison.Ordinal);
    }

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

    /// <summary>Reads up to <see cref="MaxPages"/> pages of a GitHub list.</summary>
    private async Task<List<T>> ReadPagesAsync<TPage, T>(HttpClient client, long? installationId, string path, string what,
        string notFoundCode, Func<TPage, List<T>> items, CancellationToken ct)
    {
        var all = new List<T>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var response = await SendAsync(client, new HttpRequestMessage(HttpMethod.Get,
                $"{path}?per_page={PageSize}&page={page}"), ct);
            if (installationId is { } id)
                await EnsureInstallationSuccessAsync(response, id, what, notFoundCode, ct);
            else
                await EnsureSuccessAsync(response, what, notFoundCode, ct);

            var batch = items(await ReadAsync<TPage>(response, ct));
            all.AddRange(batch);
            if (batch.Count < PageSize)
                break;
        }

        return all;
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

    /// <summary>Whether <paramref name="value"/> is a GitHub <c>owner/name</c> safe to put in a path.</summary>
    public static bool IsRepositoryFullName(string? value) =>
        value is not null && RepositoryFullNamePattern().IsMatch(value)
        && !value.EndsWith("/.", StringComparison.Ordinal) && !value.EndsWith("/..", StringComparison.Ordinal);

    /// <summary>
    /// The key as PEM, or null when it is missing or does not parse. An environment variable rarely holds
    /// several lines, so the base64 of the PEM file is accepted too.
    /// </summary>
    internal static string? ReadPrivateKey(GitHubBuildAppOptions options, ILogger logger)
    {
        if (!options.IsConfigured)
            return null;

        var pem = options.PrivateKey!.Trim();
        if (!pem.StartsWith("-----BEGIN", StringComparison.Ordinal))
        {
            try
            {
                pem = Encoding.UTF8.GetString(Convert.FromBase64String(pem)).Trim();
            }
            catch (FormatException)
            {
                // Not base64 either: the PEM import below refuses it.
            }
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return pem;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            logger.LogWarning(
                "App builds are off: {Setting} is neither an RSA private key in PEM form nor the base64 of one",
                "GitHub:BuildApp:PrivateKey");
            return null;
        }
    }

    private static string Escape(string fullName)
    {
        if (!IsRepositoryFullName(fullName))
            throw new AppBuildException(AppBuildException.RepositoryNotFound);
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

    private sealed record GitHubUser(string Login, long Id);

    private sealed record CollaboratorPermissionResponse(string? Permission, GitHubUser? User);

    private sealed record InstallationRepositoriesResponse(List<GitHubRepository> Repositories);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryFullNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}$")]
    private static partial Regex LoginPattern();

    private sealed record SecretsResponse(List<SecretName> Secrets);

    private sealed record SecretName(string Name);

    private sealed record WorkflowRunsResponse(List<GitHubWorkflowRun> WorkflowRuns);
}

/// <param name="Login">The account the App is installed on.</param>
/// <param name="UserLogin">The GitHub user who authorized the App.</param>
public sealed record GitHubUserInstallation(
    string Login,
    bool IsOrganization,
    IReadOnlyList<string> AdminRepositories,
    string UserLogin,
    long UserId);

public sealed record GitHubRepository(
    string FullName,
    bool Private,
    bool IsTemplate,
    string? DefaultBranch,
    string HtmlUrl,
    GitHubTemplateRepository? TemplateRepository,
    GitHubRepositoryPermissions? Permissions = null);

/// <summary>The caller's access to a repository, as a user-token listing reports it.</summary>
public sealed record GitHubRepositoryPermissions(bool Admin);

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
