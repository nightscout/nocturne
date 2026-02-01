#pragma warning disable ASPIREPIPELINES003 // Experimental container image APIs

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Configuration;

namespace Nocturne.Aspire.Hosting;

/// <summary>
/// Options for configuring the Demo Data Service.
/// </summary>
public class DemoServiceOptions
{
    /// <summary>
    /// The HTTP port for the demo service. Default is 0 (dynamic port assignment).
    /// </summary>
    public int Port { get; set; } = 0;

    /// <summary>
    /// The resource name for the demo service. Default is "demo-service".
    /// </summary>
    public string ResourceName { get; set; } = "demo-service";

    /// <summary>
    /// The configuration section path for demo mode settings.
    /// Default is "Parameters:DemoMode".
    /// </summary>
    public string ConfigSection { get; set; } = "Parameters:DemoMode";

    /// <summary>
    /// Whether to clear existing demo data on startup.
    /// </summary>
    public bool ClearOnStartup { get; set; } = true;

    /// <summary>
    /// Whether to regenerate demo data on startup.
    /// </summary>
    public bool RegenerateOnStartup { get; set; } = true;

    /// <summary>
    /// Number of days of historical data to backfill.
    /// </summary>
    public int BackfillDays { get; set; } = 90;

    /// <summary>
    /// Interval in minutes between generated data points.
    /// </summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Interval in minutes between full demo data resets. Set to 0 to disable.
    /// </summary>
    public int ResetIntervalMinutes { get; set; } = 0;
}

/// <summary>
/// Extension methods for adding the Demo Data Service to an Aspire application.
/// </summary>
public static class DemoServiceExtensions
{
    /// <summary>
    /// Adds the Demo Data Service to the application.
    /// The service generates synthetic glucose data for demonstrations and testing.
    /// </summary>
    /// <typeparam name="TDemoService">The demo service project type (e.g., Projects.Nocturne_Services_Demo).</typeparam>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="api">The API resource that the demo service will reference.</param>
    /// <param name="database">The database resource for the demo service to use.</param>
    /// <param name="configure">Optional configuration action for demo service options.</param>
    /// <returns>The demo service resource builder, or null if demo mode is disabled.</returns>
    public static IResourceBuilder<ProjectResource>? AddDemoService<TDemoService>(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> api,
        IResourceBuilder<IResourceWithConnectionString>? database,
        Action<DemoServiceOptions>? configure = null)
        where TDemoService : IProjectMetadata, new()
    {
        var options = new DemoServiceOptions();

        // Load options from configuration
        var configSection = builder.Configuration.GetSection(options.ConfigSection);
        var enabled = configSection.GetValue<bool>("Enabled", false);

        if (!enabled)
        {
            Console.WriteLine("[Aspire] Demo mode disabled");
            api.WithEnvironment("DemoService__Enabled", "false");
            return null;
        }

        // Apply configuration values
        options.ClearOnStartup = configSection.GetValue("ClearOnStartup", options.ClearOnStartup);
        options.RegenerateOnStartup = configSection.GetValue("RegenerateOnStartup", options.RegenerateOnStartup);
        options.BackfillDays = configSection.GetValue("BackfillDays", options.BackfillDays);
        options.IntervalMinutes = configSection.GetValue("IntervalMinutes", options.IntervalMinutes);
        options.ResetIntervalMinutes = configSection.GetValue(
            "ResetIntervalMinutes",
            options.ResetIntervalMinutes
        );

        // Apply user configuration overrides
        configure?.Invoke(options);

        Console.WriteLine($"[Aspire] Demo mode enabled - adding Demo Data Service on port {(options.Port == 0 ? "dynamic" : options.Port.ToString())}");

        var demoService = builder
            .AddProject<TDemoService>(options.ResourceName)
            .WithHttpEndpoint(port: options.Port, name: "http");

        // Configure database connection if provided
        if (database != null)
        {
            demoService
                .WaitFor(database)
                .WithReference(database);
        }

        // Pass demo configuration via environment variables
        demoService
            .WithEnvironment("DemoMode__Enabled", "true")
            .WithEnvironment("DemoMode__ClearOnStartup", options.ClearOnStartup.ToString().ToLowerInvariant())
            .WithEnvironment("DemoMode__RegenerateOnStartup", options.RegenerateOnStartup.ToString().ToLowerInvariant())
            .WithEnvironment("DemoMode__BackfillDays", options.BackfillDays.ToString())
            .WithEnvironment("DemoMode__IntervalMinutes", options.IntervalMinutes.ToString())
            .WithEnvironment("DemoMode__ResetIntervalMinutes", options.ResetIntervalMinutes.ToString());

        // API should reference demo service for health monitoring
        api.WithEnvironment("DemoService__Url", demoService.GetEndpoint("http"))
           .WithEnvironment("DemoService__Enabled", "true");

        // Configure multi-arch container build for amd64 and arm64 (supports Mac Apple Silicon)
        demoService.WithContainerBuildOptions(options =>
        {
            options.TargetPlatform = ContainerTargetPlatform.LinuxAmd64 | ContainerTargetPlatform.LinuxArm64;
        });

        return demoService;
    }
}
