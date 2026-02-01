using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nocturne.Portal.API.Models;

namespace Nocturne.Portal.API.Services;

/// <summary>
/// Generates docker-compose.yml and .env files using Aspire CLI
/// </summary>
public class DockerComposeGenerator
{
    private readonly ILogger<DockerComposeGenerator> _logger;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public DockerComposeGenerator(
        ILogger<DockerComposeGenerator> logger,
        IConfiguration configuration,
        IWebHostEnvironment environment
    )
    {
        _logger = logger;
        _configuration = configuration;
        _environment = environment;
    }

    /// <summary>
    /// Generate docker-compose.yml and .env files by invoking aspire publish
    /// </summary>
    public async Task<(string dockerCompose, string envFile)> GenerateAsync(GenerateRequest request)
    {
        // Create a temporary directory for output
        var tempDir = Path.Combine(Path.GetTempPath(), $"nocturne-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Build the configuration parameters file for aspire publish
            var configFile = await WriteConfigurationFileAsync(tempDir, request);

            // Get path to the main Nocturne Aspire Host project
            var aspireHostPath = GetAspireHostPath();

            _logger.LogInformation(
                "Running aspire publish from {AspireHostPath} to {OutputDir}",
                aspireHostPath,
                tempDir
            );

            // Invoke aspire publish with the docker-compose publisher
            var result = await RunAspirePublishAsync(aspireHostPath, tempDir, configFile);

            if (!result.Success)
            {
                _logger.LogError("Aspire publish failed: {Error}", result.Error);
                throw new InvalidOperationException(
                    $"Failed to generate configuration: {result.Error}"
                );
            }

            // Read the generated files
            var dockerComposePath = Path.Combine(tempDir, "docker-compose.yaml");
            var envPath = Path.Combine(tempDir, ".env");

            if (!File.Exists(dockerComposePath))
            {
                // Try alternate location - aspire might put it in a subdirectory
                var files = Directory
                    .GetFiles(tempDir, "docker-compose.yaml", SearchOption.AllDirectories)
                    .ToArray();
                dockerComposePath =
                    files.FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "docker-compose.yml not found in output"
                    );

                envPath = Path.Combine(Path.GetDirectoryName(dockerComposePath)!, ".env");
            }

            var dockerCompose = await File.ReadAllTextAsync(dockerComposePath);
            var envFile = File.Exists(envPath)
                ? await File.ReadAllTextAsync(envPath)
                : GenerateEnvFile(request);

            // Post-process to inject user-provided values
            envFile = InjectUserValues(envFile, request);

            return (dockerCompose, envFile);
        }
        finally
        {
            // Clean up temp directory
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temp directory {TempDir}", tempDir);
            }
        }
    }

    private string GetAspireHostPath()
    {
        // Navigate from Portal project to main Aspire Host
        var basePath = _configuration["AspireHostPath"];
        if (!string.IsNullOrEmpty(basePath))
            return basePath;

        // Default: go up from Portal API to solution root, then to Aspire Host
        var solutionRoot = Path.GetFullPath(
            Path.Combine(_environment.ContentRootPath, "..", "..", "..")
        );
        return Path.Combine(solutionRoot, "src", "Aspire", "Nocturne.Aspire.Host");
    }

    private async Task<string> WriteConfigurationFileAsync(string tempDir, GenerateRequest request)
    {
        var configPath = Path.Combine(tempDir, "publish-config.json");

        var config = new Dictionary<string, object>
        {
            ["PostgreSql:UseRemoteDatabase"] = !request.Postgres.UseContainer,
            // Optional services
            ["Parameters:OptionalServices:AspireDashboard:Enabled"] = request
                .OptionalServices
                .AspireDashboard
                .Enabled,
            ["Parameters:OptionalServices:Scalar:Enabled"] = request
                .OptionalServices
                .Scalar
                .Enabled,
            ["Parameters:OptionalServices:Watchtower:Enabled"] = request
                .OptionalServices
                .Watchtower
                .Enabled,
        };

        // Add connection string if using external database
        if (
            !request.Postgres.UseContainer
            && !string.IsNullOrEmpty(request.Postgres.ConnectionString)
        )
        {
            config["ConnectionStrings:nocturne-db"] = request.Postgres.ConnectionString;
        }

        // Enable selected connectors
        foreach (var connector in request.Connectors)
        {
            config[$"Parameters:Connectors:{connector.Type}:Enabled"] = true;

            // Add connector configuration
            foreach (var (key, value) in connector.Config)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    config[$"Parameters:Connectors:{connector.Type}:{key}"] = value;
                }
            }
        }

        // Compatibility proxy settings
        if (request.SetupType == "compatibility-proxy" && request.CompatibilityProxy != null)
        {
            config["Parameters:CompatibilityProxy:Enabled"] = true;
            config["Parameters:CompatibilityProxy:NightscoutUrl"] = request
                .CompatibilityProxy
                .NightscoutUrl;
            config["Parameters:CompatibilityProxy:NightscoutApiSecret"] = request
                .CompatibilityProxy
                .NightscoutApiSecret;
            config["Parameters:CompatibilityProxy:EnableDetailedLogging"] = request
                .CompatibilityProxy
                .EnableDetailedLogging;
        }

        // Migration settings - enable Nightscout connector
        if (request.SetupType == "migrate" && request.Migration != null)
        {
            config["Parameters:Connectors:Nightscout:Enabled"] = true;
            config["Parameters:Connectors:Nightscout:SourceEndpoint"] = request
                .Migration
                .NightscoutUrl ?? string.Empty;
            config["Parameters:Connectors:Nightscout:SourceApiSecret"] = request
                .Migration
                .NightscoutApiSecret ?? string.Empty;
        }

        var json = JsonSerializer.Serialize(
            config,
            new JsonSerializerOptions { WriteIndented = true }
        );
        await File.WriteAllTextAsync(configPath, json);

        return configPath;
    }

    private async Task<(bool Success, string Error)> RunAspirePublishAsync(
        string aspireHostPath,
        string outputDir,
        string configFile
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "aspire",
            Arguments = $"publish -o \"{outputDir}\" --non-interactive",
            WorkingDirectory = aspireHostPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Note: CreateNoWindow is intentionally not set to true.
            // The aspire CLI (using Spectre.Console) needs a valid console handle
            // to function correctly on Windows, even when running headlessly.
            // Output redirection already captures all output.
        };

        // Add configuration file as environment variable
        startInfo.Environment["ASPIRE_CONFIG_FILE"] = configFile;

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                error.AppendLine(e.Data);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait up to 2 minutes for publish to complete
            var completed = await Task.Run(() => process.WaitForExit(120_000));

            if (!completed)
            {
                process.Kill(entireProcessTree: true);
                return (false, "Aspire publish timed out after 2 minutes");
            }

            var outputText = output.ToString();
            var errorText = error.ToString();

            _logger.LogInformation("Aspire publish output: {Output}", outputText);

            if (!string.IsNullOrWhiteSpace(errorText))
            {
                _logger.LogWarning("Aspire publish stderr: {Error}", errorText);
            }

            // Check if the pipeline actually succeeded (look for success indicator)
            var pipelineSucceeded = outputText.Contains(
                "PIPELINE SUCCEEDED",
                StringComparison.OrdinalIgnoreCase
            );

            // Check if output files were generated - log what we find for debugging
            var filesInOutput = Directory.Exists(outputDir)
                ? Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();

            _logger.LogInformation(
                "Files in output directory {OutputDir}: {Files}",
                outputDir,
                filesInOutput.Length > 0
                    ? string.Join(", ", filesInOutput.Select(Path.GetFileName))
                    : "(none)"
            );

            var dockerComposeExists = filesInOutput.Any(f =>
                Path.GetFileName(f).Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase)
            );

            // If pipeline succeeded AND files exist, treat as success even if there's a console error
            // (The "handle is invalid" error is a known Windows console issue with aspire CLI/Spectre.Console)
            if (pipelineSucceeded && dockerComposeExists)
            {
                if (
                    outputText.Contains("❌")
                    || outputText.Contains("handle is invalid", StringComparison.OrdinalIgnoreCase)
                )
                {
                    _logger.LogWarning(
                        "Aspire publish had a console error but files were generated successfully. Ignoring console error."
                    );
                }
                _logger.LogInformation("Aspire publish completed successfully");
                return (true, string.Empty);
            }

            // Check for actual failures
            if (process.ExitCode != 0)
            {
                return (false, !string.IsNullOrWhiteSpace(errorText) ? errorText : outputText);
            }

            // Check stdout for error patterns (real errors, not just console issues)
            if (outputText.Contains("❌") && !pipelineSucceeded)
            {
                var lines = outputText.Split('\n');
                var errorLine = lines.FirstOrDefault(l => l.Contains("❌"));
                return (false, errorLine?.Trim() ?? "Unknown error during aspire publish");
            }

            _logger.LogInformation("Aspire publish completed successfully");
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to run aspire publish: {ex.Message}");
        }
    }

    private string InjectUserValues(string envFile, GenerateRequest request)
    {
        // Build a dictionary of values to inject
        var valuesToInject = BuildValuesDictionary(request);

        // Parse existing env file and update values
        var lines = envFile.Split('\n');
        var result = new StringBuilder();
        var processedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var trimmedLine = line.TrimEnd('\r');

            // Skip empty lines and comments, preserve them as-is
            if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
            {
                result.AppendLine(trimmedLine);
                continue;
            }

            // Parse KEY=VALUE format
            var equalsIndex = trimmedLine.IndexOf('=');
            if (equalsIndex <= 0)
            {
                result.AppendLine(trimmedLine);
                continue;
            }

            var key = trimmedLine[..equalsIndex].Trim();
            var existingValue = trimmedLine[(equalsIndex + 1)..];

            processedKeys.Add(key);

            // If we have a value to inject and the existing value is empty, inject it
            if (valuesToInject.TryGetValue(key, out var newValue))
            {
                if (string.IsNullOrEmpty(existingValue))
                {
                    result.AppendLine($"{key}={newValue}");
                    _logger.LogDebug("Injected value for {Key}", key);
                }
                else
                {
                    // Keep existing non-empty value
                    result.AppendLine(trimmedLine);
                }
            }
            else
            {
                result.AppendLine(trimmedLine);
            }
        }

        // Add any values that weren't in the original file
        var missingKeys = valuesToInject.Keys.Where(k => !processedKeys.Contains(k)).ToList();

        if (missingKeys.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("# Additional configuration");
            foreach (var key in missingKeys)
            {
                result.AppendLine($"{key}={valuesToInject[key]}");
                _logger.LogDebug("Added missing env var {Key}", key);
            }
        }

        return result.ToString();
    }

    private Dictionary<string, string> BuildValuesDictionary(GenerateRequest request)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Container images - core services
        values["NOCTURNE_API_IMAGE"] = "ghcr.io/nightscout/nocturne/api:latest";
        values["NOCTURNE_WEB_IMAGE"] = "ghcr.io/nightscout/nocturne/web:latest";

        // Default ports
        values["NOCTURNE_API_PORT"] = "8080";
        values["NOCTURNE_WEB_PORT"] = "5173";

        // Add connector images and ports
        // The env var pattern is: {SERVICE_NAME}_IMAGE and {SERVICE_NAME}_PORT
        // where SERVICE_NAME is derived from connector type (e.g., NIGHTSCOUT_CONNECTOR)
        foreach (var connector in request.Connectors)
        {
            var servicePrefix = connector.Type.ToUpperInvariant().Replace("-", "_");
            var imageEnvVar = $"{servicePrefix}_CONNECTOR_IMAGE";
            var portEnvVar = $"{servicePrefix}_CONNECTOR_PORT";
            var imageName =
                $"ghcr.io/nightscout/nocturne/{connector.Type.ToLowerInvariant()}-connector:latest";

            values[imageEnvVar] = imageName;
            values[portEnvVar] = "8080";
        }

        // Database configuration
        if (request.Postgres.UseContainer)
        {
            var password = GenerateSecurePassword();
            values["POSTGRES_USERNAME"] = "nocturne";
            values["POSTGRES_PASSWORD"] = password;
            values["NOCTURNE_POSTGRES"] =
                $"Host=nocturne-postgres-server;Port=5432;Username=nocturne;Password={password};Database=nocturne";
        }
        else if (!string.IsNullOrEmpty(request.Postgres.ConnectionString))
        {
            values["NOCTURNE_POSTGRES"] = request.Postgres.ConnectionString;
        }

        // API Secret - generate if not provided
        values["API_SECRET"] = GenerateSecurePassword();

        // Connector configuration - keys are already env var names from frontend
        foreach (var connector in request.Connectors)
        {
            foreach (var (envVarName, value) in connector.Config)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    values[envVarName] = value;
                }
            }
        }

        // Migration configuration - emit MIGRATION_* env vars for pending migration notifications
        // These enable the API to pre-populate the migration form and show notifications
        if (request.SetupType == "migrate" && request.Migration != null)
        {
            values["MIGRATION_MODE"] = request.Migration.Mode;

            if (request.Migration.Mode == "Api")
            {
                values["MIGRATION_NS_URL"] = request.Migration.NightscoutUrl ?? "";
                values["MIGRATION_NS_API_SECRET"] = request.Migration.NightscoutApiSecret ?? "";
            }
            else // MongoDb mode
            {
                values["MIGRATION_MONGO_CONNECTION_STRING"] =
                    request.Migration.MongoConnectionString ?? "";
                values["MIGRATION_MONGO_DATABASE_NAME"] = request.Migration.MongoDatabaseName ?? "";
            }

            // Also set connector env vars for ongoing sync (API mode only)
            if (
                request.Migration.Mode == "Api"
                && !string.IsNullOrEmpty(request.Migration.NightscoutUrl)
            )
            {
                values["CONNECT_NS_URL"] = request.Migration.NightscoutUrl;
                values["CONNECT_NS_API_SECRET"] = request.Migration.NightscoutApiSecret ?? "";
            }
        }

        // Compatibility proxy configuration
        if (request.SetupType == "compatibility-proxy" && request.CompatibilityProxy != null)
        {
            values["COMPAT_PROXY_ENABLED"] = "true";
            values["COMPAT_PROXY_NIGHTSCOUT_URL"] = request.CompatibilityProxy.NightscoutUrl;
            values["COMPAT_PROXY_NIGHTSCOUT_SECRET"] = request
                .CompatibilityProxy
                .NightscoutApiSecret;
            values["COMPAT_PROXY_DETAILED_LOGGING"] = request
                .CompatibilityProxy
                .EnableDetailedLogging
                ? "true"
                : "false";
        }

        return values;
    }

    private string GenerateEnvFile(GenerateRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Nocturne Configuration");
        sb.AppendLine("# Generated by Nocturne Portal");
        sb.AppendLine();

        // Database configuration
        if (request.Postgres.UseContainer)
        {
            var password = GenerateSecurePassword();
            sb.AppendLine("# PostgreSQL Configuration");
            sb.AppendLine("POSTGRES_USER=nocturne");
            sb.AppendLine($"POSTGRES_PASSWORD={password}");
            sb.AppendLine(
                $"CONNECTION_STRING=Host=postgres;Port=5432;Username=nocturne;Password={password};Database=nocturne"
            );
        }
        else
        {
            sb.AppendLine("# External Database Configuration");
            sb.AppendLine($"CONNECTION_STRING={request.Postgres.ConnectionString}");
        }
        sb.AppendLine();

        // Connector configurations - keys are already env var names
        foreach (var connector in request.Connectors)
        {
            sb.AppendLine($"# {connector.Type} Connector");
            foreach (var (envVarName, value) in connector.Config)
            {
                sb.AppendLine($"{envVarName}={value}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GenerateSecurePassword()
    {
        // Use cryptographically secure random number generator
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var result = new char[24];
        var bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);

        for (int i = 0; i < 24; i++)
        {
            result[i] = chars[bytes[i] % chars.Length];
        }

        return new string(result);
    }
}
