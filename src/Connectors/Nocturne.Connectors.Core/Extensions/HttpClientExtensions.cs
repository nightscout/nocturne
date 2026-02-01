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
    /// <summary>
    ///     Default User-Agent for Nocturne connectors
    /// </summary>
    private const string DefaultUserAgent = "Nocturne-Connect/1.0";

    extension(IHttpClientBuilder builder)
    {
        /// <summary>
        ///     Generic connector client configuration with sensible defaults.
        ///     Use this for new connectors instead of creating connector-specific extensions.
        /// </summary>
        /// <param name="baseUrl">Base URL for the API (e.g., "api.example.com" or "https://api.example.com")</param>
        /// <param name="additionalHeaders">Optional additional headers to include in all requests</param>
        /// <param name="userAgent">Custom User-Agent string (defaults to "Nocturne-Connect/1.0")</param>
        /// <param name="timeout">Request timeout (defaults to 2 minutes)</param>
        /// <param name="connectTimeout">Connection timeout (defaults to 5 seconds)</param>
        /// <param name="addResilience">Whether to add resilience policies (retry, circuit breaker)</param>
        public IHttpClientBuilder ConfigureConnectorClient(
            string baseUrl,
            Dictionary<string, string>? additionalHeaders = null,
            string? userAgent = null,
            TimeSpan? timeout = null,
            TimeSpan? connectTimeout = null,
            bool addResilience = false)
        {
            var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(2);
            var effectiveConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
            var effectiveUserAgent = userAgent ?? DefaultUserAgent;

            // Ensure URL has scheme
            var url = baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? baseUrl
                : $"https://{baseUrl}";

            builder
                .ConfigureHttpClient(client =>
                {
                    client.BaseAddress = new Uri(url);
                    client.DefaultRequestHeaders.Accept.Clear();
                    client.DefaultRequestHeaders.Accept.Add(
                        new MediaTypeWithQualityHeaderValue("application/json")
                    );
                    client.DefaultRequestHeaders.Add("User-Agent", effectiveUserAgent);
                    client.Timeout = effectiveTimeout;

                    // Add any additional headers
                    if (additionalHeaders == null) return;
                    foreach (var header in additionalHeaders)
                    {
                        client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                    }
                })
                .ConfigurePrimaryHttpMessageHandler(() =>
                    new SocketsHttpHandler
                    {
                        AutomaticDecompression = DecompressionMethods.All,
                        ConnectTimeout = effectiveConnectTimeout,
                        PooledConnectionLifetime = effectiveTimeout
                    }
                );

            if (addResilience)
            {
                builder.ConfigureConnectorResilience();
            }

            return builder;
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
        private IHttpClientBuilder ConfigureConnectorResilience(TimeSpan? attemptTimeout = null,
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