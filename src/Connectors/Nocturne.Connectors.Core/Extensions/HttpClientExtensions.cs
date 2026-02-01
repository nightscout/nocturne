using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
///     Extension methods for configuring HttpClient instances in connectors
/// </summary>
public static class HttpClientExtensions
{
    extension(IHttpClientBuilder builder)
    {
        /// <summary>
        ///     Configures standard headers for Dexcom Share API
        /// </summary>
        public IHttpClientBuilder ConfigureDexcomClient(string server
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
                        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                    }
                );
        }

        /// <summary>
        ///     Configures standard headers for LibreLinkUp API
        /// </summary>
        public IHttpClientBuilder ConfigureLibreLinkUpClient(string server
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
                        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                    }
                );
        }

        /// <summary>
        ///     Configures standard headers for Glooko API
        /// </summary>
        public IHttpClientBuilder ConfigureGlookoClient(string server
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
                        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                    }
                )
                // Configure connector-specific resilience with timeouts
                // Per-attempt timeout resets after each successful API response
                // Total timeout should be less than the minimum sync interval (5 min)
                .ConfigureConnectorResilience(
                    TimeSpan.FromMinutes(2),
                    TimeSpan.FromMinutes(5)
                );
        }

        /// <summary>
        ///     Configures resilience settings optimized for connector services that make
        ///     multiple sequential API calls. Uses longer timeouts per-request (2 minutes)
        ///     and a longer total timeout (10 minutes) to accommodate sync operations.
        /// </summary>
        /// <remarks>
        ///     The standard Aspire resilience handler has a 30-second per-request timeout
        ///     which is too short for connectors that need to fetch data from multiple
        ///     endpoints sequentially (e.g., Glooko fetches glucose, treatments, food, etc.).
        ///     This configuration:
        ///     - 2 minute timeout per individual HTTP request (AttemptTimeout)
        ///     - 10 minute total timeout for all retries (TotalRequestTimeout)
        ///     - Retries up to 3 times with exponential backoff for transient failures
        ///     - Circuit breaker to fail fast when the remote service is consistently failing
        /// </remarks>
        public IHttpClientBuilder ConfigureConnectorResilience(TimeSpan? attemptTimeout = null,
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
                                )
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
                                )
                        }
                    );

                    // Add per-attempt timeout (innermost - applies to each individual request)
                    resilienceBuilder.AddTimeout(perAttemptTimeout);
                }
            );

            return builder;
        }
    }
}
