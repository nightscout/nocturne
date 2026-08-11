using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Nocturne.Tools.Abstractions.Commands;
using Nocturne.Tools.Abstractions.Configuration;
using Nocturne.Tools.Abstractions.Services;
using Nocturne.Tools.Connect.Configuration;
using Nocturne.Tools.Connect.Services;
using Nocturne.Tools.Core.Commands;
using Spectre.Console.Cli;

namespace Nocturne.Tools.Connect.Commands;

/// <summary>
/// Command settings for the run command.
/// </summary>
public sealed class RunSettings : CommandSettings
{
    [CommandOption("-d|--daemon")]
    [Description("Run in daemon mode")]
    public bool Daemon { get; init; }

    [CommandOption("-o|--once")]
    [Description("Run sync once and exit (foreground mode)")]
    public bool Once { get; init; }

    [CommandOption("-i|--interval <MINUTES>")]
    [Description("Sync interval in minutes (daemon mode only)")]
    [DefaultValue(5)]
    public int Interval { get; init; } = 5;

    [CommandOption("--dry-run")]
    [Description("Test run without uploading data to Nightscout")]
    public bool DryRun { get; init; }

    [CommandOption("-s|--since <TIMESTAMP>")]
    [Description("Start of the sync window; defaults to a rolling three-hour lookback")]
    public DateTime? Since { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Enable verbose logging")]
    public bool Verbose { get; init; }

    [CommandOption("-f|--file <FILE>")]
    [Description("Environment file to use (.env file path)")]
    public string? File { get; init; }

    [CommandOption("--data-directory <DIRECTORY>")]
    [Description("Directory to save downloaded data to")]
    public string? DataDirectory { get; init; }
}

/// <summary>
/// Command to run Nocturne Connect data synchronization.
/// </summary>
public class RunCommand : AsyncCommand<RunSettings>
{
    private readonly ILogger<RunCommand> _logger;
    private readonly IConfigurationManager _configurationManager;
    private readonly IValidationService _validationService;
    private readonly ConnectorExecutionService _connectorExecutionService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunCommand"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="configurationManager">The configuration manager.</param>
    /// <param name="validationService">The validation service.</param>
    /// <param name="connectorExecutionService">The connector execution service.</param>
    public RunCommand(
        ILogger<RunCommand> logger,
        IConfigurationManager configurationManager,
        IValidationService validationService,
        ConnectorExecutionService connectorExecutionService
    )
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configurationManager =
            configurationManager ?? throw new ArgumentNullException(nameof(configurationManager));
        _validationService =
            validationService ?? throw new ArgumentNullException(nameof(validationService));
        _connectorExecutionService =
            connectorExecutionService
            ?? throw new ArgumentNullException(nameof(connectorExecutionService));
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteAsync(CommandContext context, RunSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            // Load and validate configuration
            var config = _configurationManager.LoadConfiguration<ConnectConfiguration>();

            if (!_configurationManager.ValidateConfiguration(config))
            {
                Console.WriteLine("❌ Configuration validation failed");
                return 1;
            }

            _logger.LogInformation("Starting Nocturne Connect");
            _logger.LogInformation("Connect Source: {Source}", config.ConnectSource);
            _logger.LogInformation("Target Nightscout: {Url}", config.NightscoutUrl);

            if (settings.DryRun)
            {
                _logger.LogInformation("Running in DRY RUN mode - no data will be uploaded");
            }

            // Execute the connector logic using the new service
            var success = await _connectorExecutionService.ExecuteConnectorAsync(
                config,
                daemon: settings.Daemon,
                once: settings.Once,
                interval: settings.Interval,
                dryRun: settings.DryRun,
                since: settings.Since?.ToUniversalTime(),
                cancellationToken: CancellationToken.None
            );

            if (success)
            {
                Console.WriteLine("✅ Sync operation completed successfully");
                return 0;
            }
            else
            {
                Console.WriteLine("❌ Sync operation failed");
                return 1;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run Nocturne Connect");
            Console.WriteLine($"❌ Error: {ex.Message}");
            return 1;
        }
    }
}
