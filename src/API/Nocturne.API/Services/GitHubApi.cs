using System.Net.Http.Headers;

namespace Nocturne.API.Services;

/// <summary>
/// The single home for the upstream repository coordinates and the GitHub
/// REST client setup. Every flow that talks to GitHub (support issues,
/// translation contributions) binds its options from the same "GitHub"
/// configuration section, so the defaults and the request headers live here
/// once rather than once per flow.
/// </summary>
public static class GitHubApi
{
    public const string DefaultOwner = "nightscout";
    public const string DefaultRepo = "nocturne";

    public static HttpClient CreateClient(IHttpClientFactory httpClientFactory, string? pat)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://api.github.com");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Nocturne", "1.0"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}
