using Nocturne.API.Services.BackgroundServices;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// Interface for querying demo mode status.
/// </summary>
public interface IDemoModeService
{
    /// <summary>
    /// Whether demo mode is enabled and the external demo service is running.
    /// When true, only demo data should be shown to users.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Whether demo mode is configured (even if the service is not yet healthy).
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Base URL of the external demo data service as configured, or null when none was injected.
    /// </summary>
    string? ServiceUrl { get; }
}

/// <summary>
/// Reads demo mode status from the two independent configuration sources and exposes a unified flag.
/// </summary>
/// <remarks>
/// <para>
/// Demo mode can be enabled by either of:
/// <list type="bullet">
///   <item>The <c>DemoService:Enabled</c> configuration key — set by Aspire via environment variables
///         when the demo data service project is included in the Aspire app host.</item>
///   <item>The <c>Parameters:DemoMode:Enabled</c> key — used in <c>appsettings.json</c> for
///         local development without Aspire.</item>
/// </list>
/// </para>
/// <para>
/// <see cref="IDemoModeService.IsConfigured"/> is only <see langword="true"/> when enabled AND
/// a non-empty <c>DemoService:Url</c> is present, indicating the external demo data service is
/// reachable. A true <c>IsEnabled</c> with false <c>IsConfigured</c> means demo mode was
/// requested but the service URL was not injected (startup misconfiguration).
/// </para>
/// </remarks>
/// <seealso cref="IDemoModeService"/>
/// <seealso cref="BackgroundServices.DemoServiceConfiguration"/>
public class DemoModeService : IDemoModeService
{
    private readonly bool _isEnabled;
    private readonly bool _isConfigured;
    private readonly string? _serviceUrl;
    private readonly ILogger<DemoModeService> _logger;

    public DemoModeService(IConfiguration configuration, ILogger<DemoModeService> logger)
    {
        _logger = logger;

        // Read from DemoService section (set by Aspire environment variables)
        var demoServiceConfig =
            configuration
                .GetSection(DemoServiceConfiguration.SectionName)
                .Get<DemoServiceConfiguration>() ?? new DemoServiceConfiguration();

        // Also check Parameters:DemoMode:Enabled (set in appsettings.json for local dev)
        var parametersDemoModeEnabled = configuration.GetValue<bool>(
            "Parameters:DemoMode:Enabled",
            false
        );

        // Demo mode is enabled if either source says so
        _isEnabled = demoServiceConfig.Enabled || parametersDemoModeEnabled;
        _serviceUrl = demoServiceConfig.Url;
        _isConfigured = _isEnabled && !string.IsNullOrWhiteSpace(_serviceUrl);

        // Log all demo service configuration for debugging
        _logger.LogInformation(
            "Demo mode service initialized - DemoService:Enabled={DemoServiceEnabled}, Parameters:DemoMode:Enabled={ParametersEnabled}, Final IsEnabled={IsEnabled}",
            demoServiceConfig.Enabled,
            parametersDemoModeEnabled,
            _isEnabled
        );
    }

    /// <inheritdoc />
    public bool IsEnabled => _isEnabled;

    /// <inheritdoc />
    public bool IsConfigured => _isConfigured;

    /// <inheritdoc />
    public string? ServiceUrl => _serviceUrl;
}
