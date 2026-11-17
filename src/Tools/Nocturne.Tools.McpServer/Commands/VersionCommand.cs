using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Nocturne.Tools.Abstractions.Services;
using Nocturne.Tools.McpServer.Configuration;
using Spectre.Console.Cli;

namespace Nocturne.Tools.McpServer.Commands;

/// <summary>
/// Command to display version information for the MCP Server tool.
/// </summary>
public class VersionCommand : AsyncCommand<VersionCommand.Settings>
{
    /// <summary>
    /// Settings for the version command.
    /// </summary>
    public class Settings : CommandSettings
    {
        /// <summary>
        /// Gets or sets a value indicating whether to show detailed version information.
        /// </summary>
        [CommandOption("-d|--detailed")]
        [Description("Whether to show detailed version information")]
        [DefaultValue(false)]
        public bool Detailed { get; init; } = false;
    }

    private readonly ILogger<VersionCommand> _logger;
    private readonly IProgressReporter _progressReporter;
    private readonly McpServerConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionCommand"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="progressReporter">The progress reporter.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="timeProvider">The clock.</param>
    public VersionCommand(
        ILogger<VersionCommand> logger,
        IProgressReporter progressReporter,
        McpServerConfiguration configuration,
        TimeProvider timeProvider
    )
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _progressReporter =
            progressReporter ?? throw new ArgumentNullException(nameof(progressReporter));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Displays version information for the MCP Server tool.
    /// </summary>
    /// <param name="context">The command context.</param>
    /// <param name="settings">The command settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString() ?? "Unknown";
            var buildDate = GetBuildDate(assembly);

            _logger.LogInformation("{ToolName} v{Version}", _configuration.ToolName, version);

            Console.WriteLine($"{_configuration.ToolName} v{version}");

            if (settings.Detailed)
            {
                Console.WriteLine($"Build Date: {buildDate:yyyy-MM-dd HH:mm:ss} UTC");
                Console.WriteLine($"Assembly: {assembly.GetName().Name}");
                Console.WriteLine($"Location: {assembly.Location}");
                Console.WriteLine($"Runtime: {Environment.Version}");
                Console.WriteLine($"Platform: {Environment.OSVersion}");
                Console.WriteLine(
                    $"Architecture: {Environment.ProcessorCount} cores, {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}"
                );

                // Show MCP Server capabilities
                Console.WriteLine("\nMCP Server Capabilities:");
                Console.WriteLine("  - Transport: stdio, SSE (Server-Sent Events)");
                Console.WriteLine("  - Protocol: Model Context Protocol (MCP)");
                Console.WriteLine("  - API Integration: Nocturne REST API");

                // Show supported tools
                Console.WriteLine("\nAvailable MCP Tools:");
                var tools = new[]
                {
                    "GetCurrentEntry - Get the most recent glucose reading",
                    "GetRecentEntries - Get recent glucose entries with filtering",
                    "GetEntriesByDateRange - Get entries within a specific date range",
                    "GetEntryById - Get a specific entry by ID",
                    "CreateEntry - Create a new glucose entry",
                    "GetGlucoseStatistics - Get glucose statistics and time in range",
                    "GetEntryCount - Get entry count statistics",
                };

                foreach (var tool in tools)
                {
                    Console.WriteLine($"  - {tool}");
                }

                // Show dependencies
                Console.WriteLine("\nKey Dependencies:");
                ShowDependencyVersion("ModelContextProtocol");
                ShowDependencyVersion("Microsoft.Extensions.Hosting");
                ShowDependencyVersion("Microsoft.Extensions.Http");
                ShowDependencyVersion("Microsoft.AspNetCore");
                ShowDependencyVersion("System.Text.Json");

                // Show environment info
                Console.WriteLine("\nEnvironment Configuration:");
                Console.WriteLine($"  Default API URL: {_configuration.ApiBaseUrl}");
                Console.WriteLine($"  Default Timeout: {_configuration.ApiTimeoutSeconds}s");
                Console.WriteLine($"  Default Port (SSE): {_configuration.Port}");
            }

            return await Task.FromResult(0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving version information: {Message}", ex.Message);
            return 1;
        }
    }

    private DateTime GetBuildDate(Assembly assembly)
    {
        try
        {
            var location = assembly.Location;
            if (!string.IsNullOrEmpty(location) && File.Exists(location))
            {
                return File.GetCreationTimeUtc(location);
            }
        }
        catch
        {
            // Fallback to a reasonable default
        }

        return _timeProvider.GetUtcNow().UtcDateTime;
    }

    private static void ShowDependencyVersion(string packageName)
    {
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var assembly = assemblies.FirstOrDefault(a =>
                a.GetName().Name?.StartsWith(packageName, StringComparison.OrdinalIgnoreCase)
                == true
            );

            if (assembly != null)
            {
                var version = assembly.GetName().Version?.ToString() ?? "Unknown";
                Console.WriteLine($"  - {packageName}: {version}");
            }
        }
        catch
        {
            Console.WriteLine($"  - {packageName}: Version unavailable");
        }
    }
}

