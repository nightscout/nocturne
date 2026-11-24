using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Nightscout;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Compatibility;

namespace Nocturne.API.Extensions;

/// <summary>
/// Extension methods for configuring compatibility proxy services
/// </summary>
public static class CompatibilityProxyServiceExtensions
{
    /// <summary>
    /// The client the manual test reads Nightscout through. It has no resilience handler: a retry
    /// would write a manual POST twice and distort the status and time the test measures.
    /// </summary>
    public const string ManualTestHttpClientName = "CompatibilityManualTest";

    /// <summary>
    /// Add compatibility proxy services to the service collection
    /// </summary>
    public static IServiceCollection AddCompatibilityProxyServices(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        // Note: NocturneDbContext is already registered via AddPostgreSqlInfrastructure()
        // Do not register it again here as it causes DI conflicts

        // Configure compatibility proxy options
        services.Configure<CompatibilityProxyConfiguration>(
            configuration.GetSection(CompatibilityProxyConfiguration.ConfigurationSection)
        );

        // Register core services
        services.AddScoped<IRequestCloningService, RequestCloningService>();
        services.AddScoped<IRequestForwardingService, RequestForwardingService>();

        services.AddScoped<ICorrelationService, CorrelationService>();
        services.AddScoped<IResponseComparisonService, ResponseComparisonService>();

        services.AddScoped<IDiscrepancyForwardingService, DiscrepancyForwardingService>();
        services.AddScoped<IDiscrepancyPersistenceService, DiscrepancyPersistenceService>();

        services.AddHostedService<DiscrepancyMaintenanceService>();

        // Configure HTTP clients for target systems
        services.AddHttpClients(configuration);

        // Add health checks for compatibility proxy-specific functionality
        services.AddHealthChecks().AddCheck<CompatibilityProxyHealthCheck>("compatibility-proxy");

        return services;
    }

    private static IServiceCollection AddHttpClients(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var proxyConfig =
            configuration
                .GetSection(CompatibilityProxyConfiguration.ConfigurationSection)
                .Get<CompatibilityProxyConfiguration>() ?? new CompatibilityProxyConfiguration();

        // Requests carry absolute URIs built by NightscoutAddress from the connector URL.
        var nightscout = services.AddHttpClient(
            "NightscoutClient",
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(proxyConfig.TimeoutSeconds);
                client.DefaultRequestHeaders.Add(
                    "User-Agent",
                    "Nocturne-CompatibilityProxy/1.0"
                );
            }
        );
        nightscout.AddForwardingResilience(TimeSpan.FromSeconds(proxyConfig.TimeoutSeconds));
        nightscout.KeepNightscoutPathOutOfTelemetry();

        services
            .AddHttpClient(
                ManualTestHttpClientName,
                client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                    client.DefaultRequestHeaders.Add("User-Agent", "Nocturne-CompatibilityProxy/1.0");
                }
            )
            .KeepNightscoutPathOutOfTelemetry();

        // Discrepancy Forwarding HTTP client
        var forwardingConfig = proxyConfig.DiscrepancyForwarding;
        services
            .AddHttpClient(
                DiscrepancyForwardingService.HttpClientName,
                client =>
                {
                    if (!string.IsNullOrEmpty(forwardingConfig.EndpointUrl))
                    {
                        client.BaseAddress = new Uri(forwardingConfig.EndpointUrl);
                    }
                    client.Timeout = TimeSpan.FromSeconds(forwardingConfig.TimeoutSeconds);
                    client.DefaultRequestHeaders.Add(
                        "User-Agent",
                        "Nocturne-DiscrepancyForwarding/1.0"
                    );
                }
            )
            .AddForwardingResilience(TimeSpan.FromSeconds(forwardingConfig.TimeoutSeconds));

        return services;
    }

    /// <summary>
    /// The standard resilience handler with an attempt timeout of <paramref name="timeout"/>, the
    /// same bound <c>HttpClient.Timeout</c> puts on the whole call, and a retry only for a
    /// <see cref="RepeatableRequest"/>: a forwarded write is never marked, so it goes out once.
    /// </summary>
    private static IHttpStandardResiliencePipelineBuilder AddForwardingResilience(
        this IHttpClientBuilder builder,
        TimeSpan timeout
    ) =>
        builder.AddStandardResilienceHandler(options =>
        {
            options.Retry.ShouldHandle = RepeatableRequest.ShouldRetry;
            options.AttemptTimeout.Timeout = timeout;
            options.TotalRequestTimeout.Timeout = timeout;
            options.CircuitBreaker.SamplingDuration = timeout * 2;
        });
}

/// <summary>
/// Health check for the compatibility proxy service
/// </summary>
public class CompatibilityProxyHealthCheck : IHealthCheck
{
    private readonly IOptions<CompatibilityProxyConfiguration> _configuration;
    private readonly NightscoutConnectorConfiguration? _nightscoutConfig;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CompatibilityProxyHealthCheck> _logger;

    /// <summary>
    /// Initializes a new instance of the CompatibilityProxyHealthCheck class
    /// </summary>
    public CompatibilityProxyHealthCheck(
        IOptions<CompatibilityProxyConfiguration> configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<CompatibilityProxyHealthCheck> logger,
        NightscoutConnectorConfiguration? nightscoutConfig = null
    )
    {
        _configuration = configuration;
        _nightscoutConfig = nightscoutConfig;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Perform the health check for the compatibility proxy service
    /// </summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var config = _configuration.Value;
            var healthData = new Dictionary<string, object>();

            var nightscoutUrl = _nightscoutConfig?.Url;

            // Check configuration - if Nightscout URL is not configured, the service is not in use
            if (string.IsNullOrEmpty(nightscoutUrl))
            {
                _logger.LogDebug(
                    "Compatibility proxy service is not configured - no Nightscout URL set"
                );
                return HealthCheckResult.Healthy("Service not configured (optional)");
            }

            // Check Nightscout connectivity
            var nightscoutHealthy = await CheckTargetHealthAsync(
                "NightscoutClient",
                nightscoutUrl,
                cancellationToken
            );
            healthData["nightscout"] = nightscoutHealthy ? "healthy" : "unhealthy";

            healthData["responseComparison"] = config.Comparison.EnableDeepComparison
                ? "enabled"
                : "disabled";
            healthData["correlationTracking"] = config.EnableCorrelationTracking
                ? "enabled"
                : "disabled";

            healthData["configuration"] = "valid";
            return HealthCheckResult.Healthy("Compatibility proxy service is healthy", healthData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            return HealthCheckResult.Unhealthy("Health check failed", ex);
        }
    }

    private async Task<bool> CheckTargetHealthAsync(
        string clientName,
        string targetUrl,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (!NightscoutAddress.TryResolve(targetUrl, out var address) || !address.TryFor("", out var probe))
            {
                _logger.LogDebug("Target {ClientName} has no usable address", clientName);
                return false;
            }

            using var client = _httpClientFactory.CreateClient(clientName);
            client.Timeout = TimeSpan.FromSeconds(5); // Short timeout for health checks

            using var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, probe),
                cancellationToken
            );

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Target {ClientName} health check failed", clientName);
            return false;
        }
    }
}
