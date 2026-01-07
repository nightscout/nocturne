using System.Diagnostics;
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
        IWebHostEnvironment environment)
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

            _logger.LogInformation("Running aspire publish from {AspireHostPath} to {OutputDir}",
                aspireHostPath, tempDir);

            // Invoke aspire publish with the docker-compose publisher
            var result = await RunAspirePublishAsync(aspireHostPath, tempDir, configFile);

            if (!result.Success)
            {
                _logger.LogError("Aspire publish failed: {Error}", result.Error);
                throw new InvalidOperationException($"Failed to generate configuration: {result.Error}");
            }

            // Read the generated files
            var dockerComposePath = Path.Combine(tempDir, "docker-compose.yml");
            var envPath = Path.Combine(tempDir, ".env");

            if (!File.Exists(dockerComposePath))
            {
                // Try alternate location - aspire might put it in a subdirectory
                var files = Directory.GetFiles(tempDir, "docker-compose.yml", SearchOption.AllDirectories);
                dockerComposePath = files.FirstOrDefault()
                    ?? throw new InvalidOperationException("docker-compose.yml not found in output");

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
        var solutionRoot = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "..", "..", ".."));
        return Path.Combine(solutionRoot, "src", "Aspire", "Nocturne.Aspire.Host");
    }

    private async Task<string> WriteConfigurationFileAsync(string tempDir, GenerateRequest request)
    {
        var configPath = Path.Combine(tempDir, "publish-config.json");

        var config = new Dictionary<string, object>
        {
            ["PostgreSql:UseRemoteDatabase"] = !request.Postgres.UseContainer
        };

        // Add connection string if using external database
        if (!request.Postgres.UseContainer && !string.IsNullOrEmpty(request.Postgres.ConnectionString))
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
            config["Parameters:CompatibilityProxy:NightscoutUrl"] = request.CompatibilityProxy.NightscoutUrl;
            config["Parameters:CompatibilityProxy:NightscoutApiSecret"] = request.CompatibilityProxy.NightscoutApiSecret;
        }

        // Migration settings - enable Nightscout connector
        if (request.SetupType == "migrate" && request.Migration != null)
        {
            config["Parameters:Connectors:Nightscout:Enabled"] = true;
            config["Parameters:Connectors:Nightscout:SourceEndpoint"] = request.Migration.NightscoutUrl;
            config["Parameters:Connectors:Nightscout:SourceApiSecret"] = request.Migration.NightscoutApiSecret;
        }

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(configPath, json);

        return configPath;
    }

    private async Task<(bool Success, string Error)> RunAspirePublishAsync(
        string aspireHostPath, string outputDir, string configFile)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "aspire",
            Arguments = $"publish --publisher docker-compose -o \"{outputDir}\"",
            WorkingDirectory = aspireHostPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Add configuration file as environment variable
        startInfo.Environment["ASPIRE_CONFIG_FILE"] = configFile;

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

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

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Aspire publish output: {Output}", output.ToString());
                return (false, error.ToString());
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
        var sb = new StringBuilder(envFile);

        // Ensure user-provided values are in the env file
        foreach (var connector in request.Connectors)
        {
            foreach (var (key, value) in connector.Config)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    var envVarName = $"CONNECT_{connector.Type.ToUpperInvariant()}_{key.ToUpperInvariant()}";
                    // Only add if not already present
                    if (!envFile.Contains(envVarName))
                    {
                        sb.AppendLine($"{envVarName}={value}");
                    }
                }
            }
        }

        return sb.ToString();
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
            sb.AppendLine($"CONNECTION_STRING=Host=postgres;Port=5432;Username=nocturne;Password={password};Database=nocturne");
        }
        else
        {
            sb.AppendLine("# External Database Configuration");
            sb.AppendLine($"CONNECTION_STRING={request.Postgres.ConnectionString}");
        }
        sb.AppendLine();

        // Connector configurations
        foreach (var connector in request.Connectors)
        {
            sb.AppendLine($"# {connector.Type} Connector");
            foreach (var (key, value) in connector.Config)
            {
                var envVarName = $"CONNECT_{connector.Type.ToUpperInvariant()}_{key.ToUpperInvariant()}";
                sb.AppendLine($"{envVarName}={value}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GenerateSecurePassword()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, 24)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }
}
