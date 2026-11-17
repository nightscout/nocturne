using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Nocturne.Tools.Abstractions.Services;
using Nocturne.Tools.Core.Commands;
using Nocturne.Tools.McpServer.Configuration;
using Nocturne.Tools.McpServer.Services;
using Nocturne.Tools.McpServer.Tools;
using Spectre.Console.Cli;

namespace Nocturne.Tools.McpServer.Commands;

/// <summary>
/// Command to start the MCP server.
/// </summary>
public class ServerCommand : AsyncCommand<ServerCommand.Settings>
{
    /// <summary>
    /// Settings for the server command.
    /// </summary>
    public class Settings : CommandSettings
    {
        /// <summary>
        /// Gets or sets a value indicating whether to use web server (SSE) transport instead of stdio.
        /// </summary>
        [CommandOption("--web")]
        [Description("Use web server (SSE) transport instead of stdio")]
        [DefaultValue(false)]
        public bool Web { get; init; } = false;

        /// <summary>
        /// Gets or sets the port for the web server when using SSE transport.
        /// </summary>
        [CommandOption("-p|--port")]
        [Description("Port for the web server when using SSE transport")]
        [DefaultValue(5000)]
        public int Port { get; init; } = 5000;

        /// <summary>
        /// Gets or sets the base URL for the Nocturne API.
        /// </summary>
        [CommandOption("--api-url")]
        [Description("Base URL for the Nocturne API")]
        [DefaultValue("http://localhost:1612")]
        public string ApiUrl { get; init; } = "http://localhost:1612";

        /// <summary>
        /// Gets or sets the timeout in seconds for API requests.
        /// </summary>
        [CommandOption("-t|--timeout")]
        [Description("Timeout in seconds for API requests")]
        [DefaultValue(30)]
        public int Timeout { get; init; } = 30;

        /// <summary>
        /// Gets or sets a value indicating whether to enable verbose logging.
        /// </summary>
        [CommandOption("-v|--verbose")]
        [Description("Enable verbose logging")]
        [DefaultValue(false)]
        public bool Verbose { get; init; } = false;

        /// <summary>
        /// Gets or sets the path to configuration file.
        /// </summary>
        [CommandOption("-c|--config")]
        [Description("Path to configuration file")]
        public string? Config { get; init; }
    }

    private readonly ILogger<ServerCommand> _logger;
    private readonly IProgressReporter _progressReporter;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerCommand"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="progressReporter">The progress reporter.</param>
    public ServerCommand(ILogger<ServerCommand> logger, IProgressReporter progressReporter)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _progressReporter =
            progressReporter ?? throw new ArgumentNullException(nameof(progressReporter));
    }

    /// <summary>
    /// Starts the MCP server with specified transport mode.
    /// </summary>
    /// <param name="context">The command context.</param>
    /// <param name="settings">The command settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = new McpServerConfiguration
            {
                UseWebServer = settings.Web,
                Port = settings.Port,
                ApiBaseUrl = settings.ApiUrl,
                ApiTimeoutSeconds = settings.Timeout,
                VerboseLogging = settings.Verbose,
                ConfigPath = settings.Config,
            };

            // Validate configuration
            var validationResult = config.ValidateConfiguration();
            if (validationResult != System.ComponentModel.DataAnnotations.ValidationResult.Success)
            {
                Console.Error.WriteLine(
                    $"Configuration validation failed: {validationResult.ErrorMessage}"
                );
                return 1;
            }

            if (config.UseWebServer)
            {
                // Web/SSE mode can safely log to stdout
                _logger.LogInformation("Starting MCP server configuration...");
                _progressReporter.ReportProgress(
                    new ProgressInfo("Server", 1, 3, "Configuring MCP server")
                );
                return await StartWebServerAsync(config, CancellationToken.None);
            }
            else
            {
                // stdio mode: stdout is reserved for JSON-RPC, no logging here
                return await StartConsoleServerAsync(config, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // Use stderr to avoid corrupting stdio transport
            Console.Error.WriteLine($"Failed to start MCP server: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Initialize all tool classes with the DI service provider.
    /// ToolBase provides the shared IApiService for all v4 tools.
    /// EntryTools has its own initialization for backward compatibility.
    /// </summary>
    private static void InitializeTools(IServiceProvider services)
    {
        ToolBase.Initialize(services);
        EntryTools.Initialize(services);
    }

    private async Task<int> StartWebServerAsync(
        McpServerConfiguration config,
        CancellationToken cancellationToken
    )
    {
        try
        {
            _logger.LogInformation(
                "Starting MCP server with SSE transport on port {Port}",
                config.Port
            );

            _progressReporter.ReportProgress(
                new ProgressInfo("Server", 2, 3, "Building web application")
            );

            var builder = WebApplication.CreateBuilder();

            // Configure logging level
            if (config.VerboseLogging)
            {
                builder.Logging.SetMinimumLevel(LogLevel.Debug);
            }

            // Add configuration sources
            ConfigureAppConfiguration(builder.Configuration, config);

            // Register services
            ConfigureServices(builder.Services, config);

            // Add MCP server with ASP.NET Core integration
            builder.Services.AddMcpServer().WithToolsFromAssembly();

            var app = builder.Build();

            // Initialize all tool classes
            InitializeTools(app.Services);

            // Configure the app
            ConfigureWebApp(app);

            _progressReporter.ReportProgress(
                new ProgressInfo("Server", 3, 3, "Starting web server")
            );

            _logger.LogInformation("MCP Server started successfully");
            _logger.LogInformation("SSE endpoint: http://localhost:{Port}/sse", config.Port);
            _logger.LogInformation("Health check: http://localhost:{Port}/health", config.Port);
            _logger.LogInformation("Server info: http://localhost:{Port}/", config.Port);

            // Configure the web server to listen on the specified port
            app.Urls.Clear();
            app.Urls.Add($"http://localhost:{config.Port}");

            await app.RunAsync(cancellationToken);

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start web server: {Message}", ex.Message);
            return 1;
        }
    }

    private async Task<int> StartConsoleServerAsync(
        McpServerConfiguration config,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // stdio transport: stdout is reserved for JSON-RPC messages only.
            // All logging and progress output must go to stderr or be suppressed.
            var builder = Host.CreateApplicationBuilder();

            // Clear all default logging providers (they write to stdout) and
            // redirect to stderr so diagnostics are still visible without
            // corrupting the MCP protocol.
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(
                config.VerboseLogging ? LogLevel.Debug : LogLevel.Warning
            );
            builder.Logging.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });

            // Add configuration sources
            ConfigureAppConfiguration(builder.Configuration, config);

            // Register services
            ConfigureServices(builder.Services, config);

            // Add MCP server services with stdio transport
            builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();

            var host = builder.Build();

            // Initialize all tool classes
            InitializeTools(host.Services);

            await host.RunAsync(cancellationToken);

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to start MCP server: {ex.Message}");
            return 1;
        }
    }

    private void ConfigureAppConfiguration(
        IConfigurationBuilder configBuilder,
        McpServerConfiguration config
    )
    {
        configBuilder
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("../../../../appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

        if (!string.IsNullOrEmpty(config.ConfigPath) && File.Exists(config.ConfigPath))
        {
            configBuilder.AddJsonFile(config.ConfigPath, optional: false, reloadOnChange: true);
        }

        configBuilder.AddEnvironmentVariables();
    }

    private void ConfigureServices(IServiceCollection services, McpServerConfiguration config)
    {
        // Register configuration
        var apiOptions = new NocturneApiOptions
        {
            BaseUrl = config.ApiBaseUrl,
            TimeoutSeconds = config.ApiTimeoutSeconds,
        };
        services.AddSingleton(apiOptions);
        services.AddSingleton(TimeProvider.System);

        // Register HTTP client for API calls
        services.AddHttpClient<IApiService, ApiService>(client =>
        {
            var baseUrl =
                Environment.GetEnvironmentVariable("NOCTURNE_API_URL") ?? config.ApiBaseUrl;
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(config.ApiTimeoutSeconds);
        });
    }

    private void ConfigureWebApp(WebApplication app)
    {
        // Map MCP SSE endpoint
        app.MapMcp();

        // Add health check endpoint
        app.MapGet("/health", () => "OK");

        // Add info endpoint showing available transports and tools
        app.MapGet(
            "/",
            () =>
                new
                {
                    service = "Nocturne MCP Server",
                    transport = "SSE",
                    endpoints = new { sse = "/sse", health = "/health" },
                    toolGroups = new
                    {
                        glucose = new[] { "GetRecentGlucose", "GetGlucoseByDateRange", "GetGlucoseStatistics", "GetMeterReadings", "GetGlucoseById" },
                        treatments = new[] { "GetRecentTreatments", "GetTreatment" },
                        insulin = new[] { "GetRecentBoluses", "GetBolusCalculations" },
                        stateSpans = new[] { "GetStateSpans", "GetPumpModes", "GetActivities", "GetConnectivity" },
                        profile = new[] { "GetProfileSummary", "GetTherapySettings", "GetBasalSchedule", "GetCarbRatioSchedule", "GetSensitivitySchedule" },
                        nutrition = new[] { "GetRecentCarbs", "GetRecentMeals", "GetFavoriteFoods" },
                        observations = new[] { "GetRecentNotes", "GetBgChecks", "GetDeviceEvents" },
                        deviceStatus = new[] { "GetApsSnapshots", "GetPumpSnapshots", "GetUploaderSnapshots" },
                        system = new[] { "GetSystemStatus", "HealthCheck", "GetSystemEvents" },
                        chartData = new[] { "GetDashboardData" },
                        legacy = new[] { "GetCurrentEntry", "GetRecentEntries", "GetEntriesByDateRange", "GetEntryById", "GetLegacyGlucoseStatistics", "CreateEntry", "GetEntryCount" },
                    },
                }
        );
    }
}
