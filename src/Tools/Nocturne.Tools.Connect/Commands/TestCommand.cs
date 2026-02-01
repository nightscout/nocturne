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
/// Command settings for the test command.
/// </summary>
public sealed class TestSettings : CommandSettings
{
    [CommandOption("-s|--source")]
    [Description("Test only the data source connection")]
    public bool Source { get; init; }

    [CommandOption("-n|--nightscout")]
    [Description("Test only the Nightscout connection")]
    public bool Nightscout { get; init; }

    [CommandOption("-f|--file <FILE>")]
    [Description("Environment file to use (.env file path)")]
    public string? File { get; init; }
}

/// <summary>
/// Command to test connections to data source and Nightscout.
/// </summary>
public class TestCommand : AsyncCommand<TestSettings>
{
    private readonly ILogger<TestCommand> _logger;
    private readonly IConfigurationManager _configurationManager;
    private readonly IConnectionTestService _connectionTestService;
    private readonly ConnectorTestService _connectorTestService;

    /// <summary>
    /// Initializes a new instance of the <see cref="TestCommand"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="configurationManager">The configuration manager.</param>
    /// <param name="connectionTestService">The connection test service.</param>
    /// <param name="connectorTestService">The connector test service.</param>
    public TestCommand(
        ILogger<TestCommand> logger,
        IConfigurationManager configurationManager,
        IConnectionTestService connectionTestService,
        ConnectorTestService connectorTestService
    )
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configurationManager =
            configurationManager ?? throw new ArgumentNullException(nameof(configurationManager));
        _connectionTestService =
            connectionTestService ?? throw new ArgumentNullException(nameof(connectionTestService));
        _connectorTestService =
            connectorTestService ?? throw new ArgumentNullException(nameof(connectorTestService));
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteAsync(CommandContext context, TestSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = _configurationManager.LoadConfiguration<ConnectConfiguration>();

            if (!_configurationManager.ValidateConfiguration(config))
            {
                Console.WriteLine("❌ Configuration validation failed");
                return 1;
            }

            Console.WriteLine("🔍 Testing Nocturne Connect configuration...");
            Console.WriteLine($"   Source: {config.ConnectSource}");
            Console.WriteLine($"   Target: {config.NightscoutUrl}");
            Console.WriteLine();

            var allTestsPassed = true;

            // Test data source connection
            if (!settings.Nightscout)
            {
                Console.WriteLine("🔄 Testing data source connection...");
                var sourceResult = await TestDataSourceConnection(config, CancellationToken.None);

                if (sourceResult.Success)
                {
                    Console.WriteLine($"✅ Data source connection successful");
                    if (!string.IsNullOrWhiteSpace(sourceResult.Message))
                        Console.WriteLine($"   {sourceResult.Message}");
                }
                else
                {
                    Console.WriteLine($"❌ Data source connection failed: {sourceResult.Message}");
                    allTestsPassed = false;
                }
            }

            // Test Nightscout connection
            if (!settings.Source)
            {
                Console.WriteLine("🔄 Testing Nightscout connection...");
                var nightscoutResult = await _connectionTestService.TestHttpEndpointAsync(
                    config.NightscoutUrl,
                    config.NightscoutApiSecret,
                    CancellationToken.None
                );

                if (nightscoutResult.Success)
                {
                    Console.WriteLine($"✅ Nightscout connection successful");
                    if (!string.IsNullOrWhiteSpace(nightscoutResult.Message))
                        Console.WriteLine($"   {nightscoutResult.Message}");
                }
                else
                {
                    Console.WriteLine(
                        $"❌ Nightscout connection failed: {nightscoutResult.Message}"
                    );
                    allTestsPassed = false;
                }
            }

            if (allTestsPassed)
            {
                Console.WriteLine(
                    "\n🎉 All connection tests passed! Your configuration looks ready to use."
                );
                Console.WriteLine("   Run 'nocturne-connect run' to start syncing data.");
                return 0;
            }
            else
            {
                Console.WriteLine(
                    "\n⚠️  Some connection tests failed. Please check your configuration and try again."
                );
                return 1;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test connections");
            Console.WriteLine($"❌ Error: {ex.Message}");
            return 1;
        }
    }

    private async Task<ConnectionTestResult> TestDataSourceConnection(
        ConnectConfiguration config,
        CancellationToken cancellationToken
    )
    {
        // For now, we'll do basic validation
        // In a full implementation, this would test the actual data source connections

        return config.ConnectSource?.ToLowerInvariant() switch
        {
            "glooko" => await TestGlookoConnection(config, cancellationToken),
            "dexcomshare" => await TestDexcomConnection(config, cancellationToken),
            "linkup" => await TestLibreConnection(config, cancellationToken),
            _ => new ConnectionTestResult(
                false,
                $"Unknown data source: {config.ConnectSource}",
                TimeSpan.Zero
            ),
        };
    }

    private async Task<ConnectionTestResult> TestGlookoConnection(
        ConnectConfiguration config,
        CancellationToken cancellationToken
    )
    {
        if (
            string.IsNullOrWhiteSpace(config.GlookoEmail)
            || string.IsNullOrWhiteSpace(config.GlookoPassword)
        )
        {
            return new ConnectionTestResult(
                false,
                "Glooko credentials not configured",
                TimeSpan.Zero
            );
        }

        // Use actual Glooko connector service for authentication test
        return await _connectorTestService.TestGlookoConnectionAsync(config, cancellationToken);
    }

    private async Task<ConnectionTestResult> TestDexcomConnection(
        ConnectConfiguration config,
        CancellationToken cancellationToken
    )
    {
        if (
            string.IsNullOrWhiteSpace(config.DexcomUsername)
            || string.IsNullOrWhiteSpace(config.DexcomPassword)
        )
        {
            return new ConnectionTestResult(
                false,
                "Dexcom credentials not configured",
                TimeSpan.Zero
            );
        }

        // Use actual Dexcom connector service for authentication test
        return await _connectorTestService.TestDexcomConnectionAsync(config, cancellationToken);
    }

    private async Task<ConnectionTestResult> TestLibreConnection(
        ConnectConfiguration config,
        CancellationToken cancellationToken
    )
    {
        if (
            string.IsNullOrWhiteSpace(config.LibreUsername)
            || string.IsNullOrWhiteSpace(config.LibrePassword)
        )
        {
            return new ConnectionTestResult(
                false,
                "LibreLinkUp credentials not configured",
                TimeSpan.Zero
            );
        }

        // Use actual LibreLinkUp connector service for authentication test
        return await _connectorTestService.TestLibreLinkUpConnectionAsync(
            config,
            cancellationToken
        );
    }

}
