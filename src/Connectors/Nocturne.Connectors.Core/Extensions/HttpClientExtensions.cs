using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

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
                client.DefaultRequestHeaders.Add("Version", "4.16.0");
                client.DefaultRequestHeaders.Add("Product", "llu.android");
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
                client.Timeout = TimeSpan.FromMinutes(5);
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(15),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                }
            )
            // Configure connector-specific resilience with timeouts
            // Per-attempt timeout resets after each successful API response
            // Total timeout should be less than the minimum sync interval (5 min)
            .ConfigureConnectorResilience(
                attemptTimeout: TimeSpan.FromMinutes(2),
                totalTimeout: TimeSpan.FromMinutes(5)
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
    /// Configures standard headers for MyFitnessPal API.
    /// Uses HttpClientHandler with modified TLS settings to bypass Cloudflare protection.
    /// </summary>
    public static IHttpClientBuilder ConfigureMyFitnessPalClient(this IHttpClientBuilder builder)
    {
        return builder
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                // Spoof curl User-Agent
                client.DefaultRequestHeaders.Add("User-Agent", "curl/8.4.0");
                client.Timeout = TimeSpan.FromMinutes(2);
                // Force HTTP/1.1 like curl
                client.DefaultRequestVersion = new Version(1, 1);
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    // Use TLS 1.2 like curl typically does
                    SslProtocols = System.Security.Authentication.SslProtocols.Tls12,
                    // Accept all certificates (curl default behavior with -k)
                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                        true,
                };
                return handler;
            });
    }

    private static string HashApiSecret(string apiSecret)
    {
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Configures resilience settings optimized for connector services that make
    /// multiple sequential API calls. Uses longer timeouts per-request (2 minutes)
    /// and a longer total timeout (10 minutes) to accommodate sync operations.
    /// </summary>
    /// <remarks>
    /// The standard Aspire resilience handler has a 30-second per-request timeout
    /// which is too short for connectors that need to fetch data from multiple
    /// endpoints sequentially (e.g., Glooko fetches glucose, treatments, food, etc.).
    ///
    /// This configuration:
    /// - 2 minute timeout per individual HTTP request (AttemptTimeout)
    /// - 10 minute total timeout for all retries (TotalRequestTimeout)
    /// - Retries up to 3 times with exponential backoff for transient failures
    /// - Circuit breaker to fail fast when the remote service is consistently failing
    /// </remarks>
    public static IHttpClientBuilder ConfigureConnectorResilience(
        this IHttpClientBuilder builder,
        TimeSpan? attemptTimeout = null,
        TimeSpan? totalTimeout = null
    )
    {
        var perAttemptTimeout = attemptTimeout ?? TimeSpan.FromMinutes(2);
        var totalRequestTimeout = totalTimeout ?? TimeSpan.FromMinutes(10);

        builder.AddResilienceHandler(
            "ConnectorResilience",
            resilienceBuilder =>
            {
                // Add total request timeout (outermost - applies to all retries)
                resilienceBuilder.AddTimeout(totalRequestTimeout);

                // Add retry with exponential backoff for transient failures
                resilienceBuilder.AddRetry(
                    new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = 3,
                        Delay = TimeSpan.FromSeconds(2),
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        ShouldHandle = args =>
                            ValueTask.FromResult(
                                HttpClientResiliencePredicates.IsTransient(args.Outcome)
                            ),
                    }
                );

                // Add circuit breaker
                resilienceBuilder.AddCircuitBreaker(
                    new HttpCircuitBreakerStrategyOptions
                    {
                        SamplingDuration = TimeSpan.FromSeconds(60),
                        FailureRatio = 0.5,
                        MinimumThroughput = 5,
                        BreakDuration = TimeSpan.FromSeconds(30),
                        ShouldHandle = args =>
                            ValueTask.FromResult(
                                HttpClientResiliencePredicates.IsTransient(args.Outcome)
                            ),
                    }
                );

                // Add per-attempt timeout (innermost - applies to each individual request)
                resilienceBuilder.AddTimeout(perAttemptTimeout);
            }
        );

        return builder;
    }
}
