using Microsoft.Extensions.Logging;
using Nocturne.Tools.Abstractions.Services;

namespace Nocturne.Tools.Connect.Services;

/// <summary>
/// Service for interactive setup of Nocturne Connect.
/// </summary>
public class InteractiveSetupService
{
    private readonly ILogger _logger;
    private readonly IProgressReporter _progressReporter;

    /// <summary>
    /// Initializes a new instance of the <see cref="InteractiveSetupService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="progressReporter">The progress reporter.</param>
    public InteractiveSetupService(ILogger logger, IProgressReporter progressReporter)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _progressReporter =
            progressReporter ?? throw new ArgumentNullException(nameof(progressReporter));
    }

    /// <summary>
    /// Runs the interactive setup process.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RunInteractiveSetupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting interactive setup");

            Console.WriteLine("=== Interactive Nocturne Connect Setup ===");
            Console.WriteLine(
                "This will guide you through setting up your Nocturne Connect configuration."
            );
            Console.WriteLine("Press Ctrl+C at any time to cancel.");
            Console.WriteLine();

            _progressReporter.ReportProgress(
                new ProgressInfo("Setup", 1, 5, "Gathering basic configuration")
            );

            // Basic configuration
            var source = PromptForDataSource();
            var nightscoutUrl = PromptForNightscoutUrl();
            var apiSecret = PromptForApiSecret();

            _progressReporter.ReportProgress(
                new ProgressInfo("Setup", 2, 5, "Configuring data source")
            );

            Console.WriteLine();
            Console.WriteLine("📋 Source-specific configuration:");

            // Source-specific configuration
            switch (source)
            {
                case "glooko":
                    await SetupGlookoAsync(cancellationToken);
                    break;
                case "dexcomshare":
                    await SetupDexcomAsync(cancellationToken);
                    break;
                case "linkup":
                    await SetupLibreAsync(cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown source: {source}");
            }

            _progressReporter.ReportProgress(
                new ProgressInfo("Setup", 3, 5, "Generating configuration")
            );

            Console.WriteLine();
            Console.WriteLine("✅ Configuration complete!");
            Console.WriteLine();
            Console.WriteLine("📋 Add these core variables to your appsettings.json:");
            Console.WriteLine($"\"NightscoutUrl\": \"{nightscoutUrl}\",");
            Console.WriteLine($"\"NightscoutApiSecret\": \"{apiSecret}\",");
            Console.WriteLine($"\"ConnectSource\": \"{source}\"");

            _progressReporter.ReportProgress(new ProgressInfo("Setup", 4, 5, "Finalizing setup"));

            Console.WriteLine();
            Console.WriteLine("🎉 Setup complete! Next steps:");
            Console.WriteLine("   1. Update your appsettings.json with the configuration above");
            Console.WriteLine("   2. Run 'nocturne-connect test' to verify connections");
            Console.WriteLine("   3. Run 'nocturne-connect run' to start syncing");

            _progressReporter.ReportProgress(new ProgressInfo("Setup", 5, 5, "Setup completed"));
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n❌ Setup cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Interactive setup failed");
            Console.WriteLine($"\n❌ Setup failed: {ex.Message}");
            throw;
        }
    }

    private string PromptForDataSource()
    {
        Console.WriteLine("Available data sources:");
        Console.WriteLine("  1. glooko          - Glooko diabetes platform");
        Console.WriteLine("  2. dexcomshare     - Dexcom Share");
        Console.WriteLine("  3. linkup          - LibreLinkUp (FreeStyle Libre)");
        Console.WriteLine("  4. mylife          - MyLife");
        Console.WriteLine();

        while (true)
        {
            Console.Write("Select your data source (1-4 or name): ");
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("❌ Please enter a selection.");
                continue;
            }

            var source = input switch
            {
                "1" => "glooko",
                "2" => "dexcomshare",
                "3" => "linkup",
                "4" => "mylife",
                _ => input.ToLowerInvariant(),
            };

            var validSources = new[]
            {
                "glooko",
                "dexcomshare",
                "linkup",
                "mylife",
            };
            if (validSources.Contains(source))
            {
                Console.WriteLine($"✅ Selected: {source}");
                return source;
            }

            Console.WriteLine(
                $"❌ Invalid source '{input}'. Please select 1-4 or enter a valid source name."
            );
        }
    }

    private string PromptForNightscoutUrl()
    {
        Console.WriteLine();
        while (true)
        {
            Console.Write("Enter your Nightscout URL (https://your-site.com): ");
            var url = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(url))
            {
                Console.WriteLine("❌ Nightscout URL is required.");
                continue;
            }

            if (!url.StartsWith("http"))
            {
                url = "https://" + url;
            }

            if (
                Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == "http" || uri.Scheme == "https")
            )
            {
                Console.WriteLine($"✅ Nightscout URL: {url}");
                return url;
            }

            Console.WriteLine("❌ Invalid URL format. Please enter a valid URL.");
        }
    }

    private string PromptForApiSecret()
    {
        while (true)
        {
            Console.Write("Enter your Nightscout API Secret: ");
            var secret = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(secret))
            {
                Console.WriteLine("❌ API Secret is required for uploading data.");
                continue;
            }

            if (secret.Length < 12)
            {
                Console.Write(
                    "⚠️  Warning: API Secret seems short. Are you sure this is correct? (y/N): "
                );
                var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (confirm != "y" && confirm != "yes")
                {
                    continue;
                }
            }

            Console.WriteLine("✅ API Secret configured");
            return secret;
        }
    }

    private Task SetupGlookoAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("📧 Glooko Configuration:");
        Console.WriteLine("Add these properties to your appsettings.json:");
        Console.WriteLine("\"GlookoEmail\": \"your-email@example.com\",");
        Console.WriteLine("\"GlookoPassword\": \"your-password\",");
        Console.WriteLine("\"GlookoServer\": \"eu.api.glooko.com\",");
        Console.WriteLine("\"GlookoTimezoneOffset\": 0");
        return Task.CompletedTask;
    }

    private Task SetupDexcomAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("📱 Dexcom Share Configuration:");
        Console.WriteLine("Add these properties to your appsettings.json:");
        Console.WriteLine("\"DexcomUsername\": \"your-username\",");
        Console.WriteLine("\"DexcomPassword\": \"your-password\",");
        Console.WriteLine("\"DexcomRegion\": \"us\"");
        return Task.CompletedTask;
    }

    private Task SetupLibreAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("🔗 LibreLinkUp Configuration:");
        Console.WriteLine("Add these properties to your appsettings.json:");
        Console.WriteLine("\"LibreUsername\": \"your-username\",");
        Console.WriteLine("\"LibrePassword\": \"your-password\",");
        Console.WriteLine("\"LibreRegion\": \"EU\"");
        return Task.CompletedTask;
    }

}
