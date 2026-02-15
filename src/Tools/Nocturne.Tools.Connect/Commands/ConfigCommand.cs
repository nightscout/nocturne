using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Nocturne.Tools.Abstractions.Commands;
using Nocturne.Tools.Abstractions.Configuration;
using Nocturne.Tools.Abstractions.Services;
using Nocturne.Tools.Connect.Configuration;
using Nocturne.Tools.Core.Commands;
using Spectre.Console.Cli;

namespace Nocturne.Tools.Connect.Commands;

/// <summary>
/// Command settings for the config command.
/// </summary>
public sealed class ConfigSettings : CommandSettings
{
    [CommandOption("-v|--validate")]
    [Description("Validate configuration")]
    public bool ValidateConfig { get; init; }

    [CommandOption("-f|--file <FILE>")]
    [Description("Environment file to use (.env file path)")]
    public string? File { get; init; }
}

/// <summary>
/// Command to display and validate Nocturne Connect configuration.
/// </summary>
public class ConfigCommand : AsyncCommand<ConfigSettings>
{
    private readonly ILogger<ConfigCommand> _logger;
    private readonly IConfigurationManager _configurationManager;
    private readonly IValidationService _validationService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigCommand"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="configurationManager">The configuration manager.</param>
    /// <param name="validationService">The validation service.</param>
    public ConfigCommand(
        ILogger<ConfigCommand> logger,
        IConfigurationManager configurationManager,
        IValidationService validationService
    )
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configurationManager =
            configurationManager ?? throw new ArgumentNullException(nameof(configurationManager));
        _validationService =
            validationService ?? throw new ArgumentNullException(nameof(validationService));
    }

    /// <inheritdoc/>
    public override Task<int> ExecuteAsync(CommandContext context, ConfigSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = _configurationManager.LoadConfiguration<ConnectConfiguration>();

            // Display configuration
            Console.WriteLine("=== Nocturne Connect Configuration ===");
            Console.WriteLine($"📍 Connect Source: {config.ConnectSource}");
            Console.WriteLine($"🎯 Nightscout URL: {config.NightscoutUrl}");
            Console.WriteLine($"📊 Display Units: {config.DisplayUnits}");
            Console.WriteLine($"🌐 Language: {config.Language}");
            Console.WriteLine($"🖥️  Hostname: {config.Hostname}");
            Console.WriteLine($"🔌 Port: {config.Port}");
            Console.WriteLine($"⚙️  Environment: {config.NodeEnvironment}");

            DisplaySourceSpecificConfiguration(config);

            Console.WriteLine("=====================================");

            if (settings.ValidateConfig)
            {
                Console.WriteLine("\n🔍 Validating configuration...");

                if (_configurationManager.ValidateConfiguration(config))
                {
                    Console.WriteLine("✅ Configuration validation passed!");
                    return Task.FromResult(0);
                }
                else
                {
                    Console.WriteLine("❌ Configuration validation failed!");
                    return Task.FromResult(1);
                }
            }

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to display configuration");
            Console.WriteLine($"❌ Error: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private void DisplaySourceSpecificConfiguration(ConnectConfiguration config)
    {
        switch (config.ConnectSource?.ToLowerInvariant())
        {
            case "glooko":
                Console.WriteLine();
                Console.WriteLine("📧 Glooko Configuration:");
                Console.WriteLine($"   Email: {MaskSensitiveValue(config.GlookoEmail)}");
                Console.WriteLine($"   Server: {config.GlookoServer}");
                Console.WriteLine($"   Timezone Offset: {config.GlookoTimezoneOffset}");
                break;


            case "dexcomshare":
                Console.WriteLine();
                Console.WriteLine("📱 Dexcom Share Configuration:");
                Console.WriteLine($"   Username: {MaskSensitiveValue(config.DexcomUsername)}");
                Console.WriteLine($"   Region: {config.DexcomRegion}");
                break;

            case "linkup":
                Console.WriteLine();
                Console.WriteLine("🔗 LibreLinkUp Configuration:");
                Console.WriteLine($"   Username: {MaskSensitiveValue(config.LibreUsername)}");
                Console.WriteLine($"   Region: {config.LibreRegion}");
                break;

        }
    }

    private static string MaskSensitiveValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(not set)";

        if (value.Length <= 4)
            return "****";

        return value.Substring(0, Math.Min(3, value.Length))
            + "****"
            + value.Substring(Math.Max(3, value.Length - 2));
    }
}
