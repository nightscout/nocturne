using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Platform.Releases;

/// <summary>
/// What the deployment's automatic updater will do with a new release.
/// </summary>
public interface IAutoUpdateProbe
{
    Task<AutoUpdateStatusDto> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Finds the deployment's Watchtower over the stack network and reads what it will do with a new
/// image, without access to the Docker socket.
/// </summary>
/// <remarks>
/// The compose service name resolves only while the container runs, so a resolved name is a
/// running Watchtower. Its read-only HTTP API then gives <c>monitor_only</c> (<c>/v1/config</c>)
/// and the time of its last scan (<c>/v1/history</c>). It reports neither its poll interval nor its
/// next run, so the interval comes from <see cref="WatchtowerOptions.PollIntervalSeconds"/>, the
/// value the bundle gives both containers.
/// </remarks>
public class WatchtowerProbe(
    IHttpClientFactory httpClientFactory,
    IOptions<ReleaseCheckOptions> options,
    TimeProvider timeProvider,
    ILogger<WatchtowerProbe> logger) : IAutoUpdateProbe
{
    public const string HttpClientName = "Watchtower";

    public async Task<AutoUpdateStatusDto> ProbeAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.Watchtower;
        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var baseUri)
            || !await ResolvesAsync(baseUri, cancellationToken))
            return new AutoUpdateStatusDto { Mode = AutoUpdateMode.NotDetected };

        var interval = settings.PollIntervalSeconds > 0 ? settings.PollIntervalSeconds : (int?)null;
        var detected = new AutoUpdateStatusDto { Mode = AutoUpdateMode.Detected, CheckIntervalSeconds = interval };
        if (string.IsNullOrEmpty(settings.Token))
            return detected;

        using var client = httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress = baseUri;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);

        WatchtowerConfigResponse? config;
        WatchtowerHistoryResponse? history;
        try
        {
            config = await GetAsync<WatchtowerConfigResponse>(client, "v1/config", cancellationToken);
            history = await GetAsync<WatchtowerHistoryResponse>(client, "v1/history?limit=1", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogDebug(ex, "Watchtower resolved but its HTTP API did not answer");
            return detected;
        }

        if (config?.Config is null)
            return detected;

        var lastCheck = history?.Entries.LastOrDefault()?.Timestamp;
        return new AutoUpdateStatusDto
        {
            Mode = config.Config.MonitorOnly ? AutoUpdateMode.MonitorOnly : AutoUpdateMode.Automatic,
            LastCheckAt = lastCheck,
            CheckIntervalSeconds = interval,
            NextCheckAt = NextCheck(lastCheck, interval, timeProvider.GetUtcNow()),
        };
    }

    /// <summary>
    /// The last check plus one interval. Null when either is unknown, or when that time has passed
    /// without a newer check, which means the interval configured here is not the one Watchtower runs.
    /// </summary>
    internal static DateTimeOffset? NextCheck(DateTimeOffset? lastCheck, int? intervalSeconds, DateTimeOffset now)
    {
        if (lastCheck is not { } last || intervalSeconds is not { } seconds)
            return null;

        var next = last.AddSeconds(seconds);
        return next > now ? next : null;
    }

    private static async Task<bool> ResolvesAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            return true;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
            return addresses.Length > 0;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task<T?> GetAsync<T>(HttpClient client, string path, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await client.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("Watchtower returned {Status} for {Path}", (int)response.StatusCode, path);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private sealed class WatchtowerConfigResponse
    {
        [JsonPropertyName("config")]
        public WatchtowerConfig? Config { get; set; }
    }

    private sealed class WatchtowerConfig
    {
        [JsonPropertyName("monitor_only")]
        public bool MonitorOnly { get; set; }
    }

    private sealed class WatchtowerHistoryResponse
    {
        [JsonPropertyName("entries")]
        public List<WatchtowerHistoryEntry> Entries { get; set; } = [];
    }

    private sealed class WatchtowerHistoryEntry
    {
        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }
    }
}
