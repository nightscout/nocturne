using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
/// Extension methods for configuring HttpClient instances in connectors
/// </summary>
public static class HttpClientExtensions
{
    /// <summary>
    /// Configures standard headers for Dexcom Share API
    /// </summary>
    public static IHttpClientBuilder ConfigureDexcomClient(
        this IHttpClientBuilder builder,
        string server
    )
    {
        return builder
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri($"https://{server}");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json")
                );
                client.DefaultRequestHeaders.Add("User-Agent", "Nocturne-Connect/1.0");
                client.Timeout = TimeSpan.FromMinutes(2);
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                }
            );
    }

    /// <summary>
    /// Configures standard headers for Nightscout API
    /// </summary>
    public static IHttpClientBuilder ConfigureNightscoutClient(
        this IHttpClientBuilder builder,
        string sourceUrl,
        string? apiSecret = null
    )
    {
        return builder
            .ConfigureHttpClient(client =>
            {
                // Ensure URL has a scheme - prepend https:// if missing (consistent with other connectors)
                var normalizedUrl = sourceUrl.TrimEnd('/');
                if (
                    !normalizedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !normalizedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                )
                {
                    normalizedUrl = $"https://{normalizedUrl}";
                }

                client.BaseAddress = new Uri(normalizedUrl);
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json")
                );
                client.DefaultRequestHeaders.Add("User-Agent", "Nocturne-Connect/1.0");

                if (!string.IsNullOrEmpty(apiSecret))
                {
                    var hashedSecret = HashApiSecret(apiSecret);
                    client.DefaultRequestHeaders.Add("API-SECRET", hashedSecret);
                }

                client.Timeout = TimeSpan.FromMinutes(2);
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                }
            );
    }

    /// <summary>
    /// Configures standard headers for LibreLinkUp API
    /// </summary>
    public static IHttpClientBuilder ConfigureLibreLinkUpClient(
        this IHttpClientBuilder builder,
        string server
    )
    {
        return builder
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri($"https://{server}");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json")
                );
                client.DefaultRequestHeaders.Add("User-Agent", "Nocturne-Connect/1.0");
                client.DefaultRequestHeaders.Add("version", "4.7.0");
                client.DefaultRequestHeaders.Add("product", "llu.android");
                client.Timeout = TimeSpan.FromMinutes(2);
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                }
            );
    }

    /// <summary>
    /// Configures standard headers for Glooko API
    /// </summary>
    public static IHttpClientBuilder ConfigureGlookoClient(
        this IHttpClientBuilder builder,
        string server
    )
    {
        return builder
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri($"https://{server}");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json")
                );
                client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
                client.DefaultRequestHeaders.Add(
                    "User-Agent",
                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15"
                );
                client.Timeout = TimeSpan.FromMinutes(2);
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                }
            );
    }

    /// <summary>
    /// Configures standard headers for CareLink API
    /// </summary>
    public static IHttpClientBuilder ConfigureCareLinkClient(
        this IHttpClientBuilder builder,
        string server
    )
    {
        return builder
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri($"https://{server}");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("text/html")
                );
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/xhtml+xml")
                );
                client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
                client.DefaultRequestHeaders.Add("Accept-Language", "en;q=0.9, *;q=0.8");
                client.DefaultRequestHeaders.Add(
                    "User-Agent",
                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15"
                );
                client.Timeout = TimeSpan.FromMinutes(2);
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                }
            );
    }

    /// <summary>
    /// Configures standard headers for MyFitnessPal API
    /// </summary>
    public static IHttpClientBuilder ConfigureMyFitnessPalClient(this IHttpClientBuilder builder)
    {
        return builder
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json")
                );
                client.DefaultRequestHeaders.Add(
                    "User-Agent",
                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15"
                );
                client.Timeout = TimeSpan.FromMinutes(2);
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                }
            );
    }

    private static string HashApiSecret(string apiSecret)
    {
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
