using Microsoft.Extensions.DependencyInjection;
using Nocturne.Tools.Core;
using Nocturne.Tools.McpServer.Commands;
using Nocturne.Tools.McpServer.Configuration;
using Nocturne.Tools.McpServer.Services;

namespace Nocturne.Tools.McpServer;

/// <summary>
/// Main entry point for the Nocturne MCP Server tool.
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Check for legacy arguments and convert them to modern format
        args = ConvertLegacyArguments(args);

        // Create and configure the application
        var app = SpectreApplicationBuilder
            .Create("Nocturne MCP Server")
            .ConfigureLogging()
            .ConfigureCoreServices()
            .ConfigureServices(services =>
            {
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<McpServerConfiguration>();
                services.AddTransient<IApiService, ApiService>();
                services.AddSingleton<NocturneApiOptions>();
            })
            .Configure(config =>
            {
                config
                    .AddCommand<ServerCommand>("server")
                    .WithDescription("Start the MCP server with specified transport mode");

                config
                    .AddCommand<VersionCommand>("version")
                    .WithDescription("Display version information for the MCP Server tool");
            })
            .Build();

        return await app.RunAsync(args);
    }

    /// <summary>
    /// Converts legacy command line arguments to modern format.
    /// This maintains backward compatibility with existing scripts and documentation.
    /// </summary>
    /// <param name="args">Original command line arguments.</param>
    /// <returns>Converted arguments for Spectre.Console.Cli.</returns>
    private static string[] ConvertLegacyArguments(string[] args)
    {
        var convertedArgs = new List<string>();

        // Always default to server command unless version is explicitly requested
        var hasExplicitCommand = args.Any(arg => arg == "version" || arg == "server");
        if (!hasExplicitCommand)
        {
            convertedArgs.Add("server");
        }

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg.ToLowerInvariant())
            {
                case "--web":
                case "--sse":
                    convertedArgs.Add("--web");
                    break;

                case "--stdio":
                    // Default behavior, no need to add anything
                    break;

                case "--port":
                case "-p":
                    convertedArgs.Add("-p");
                    if (i + 1 < args.Length)
                    {
                        convertedArgs.Add(args[++i]);
                    }
                    break;

                case "--api-url":
                    convertedArgs.Add("--api-url");
                    if (i + 1 < args.Length)
                    {
                        convertedArgs.Add(args[++i]);
                    }
                    break;

                case "--timeout":
                case "-t":
                    convertedArgs.Add("-t");
                    if (i + 1 < args.Length)
                    {
                        convertedArgs.Add(args[++i]);
                    }
                    break;

                case "--verbose":
                case "-v":
                    convertedArgs.Add("-v");
                    break;

                case "--config":
                case "-c":
                    convertedArgs.Add("-c");
                    if (i + 1 < args.Length)
                    {
                        convertedArgs.Add(args[++i]);
                    }
                    break;

                case "--version":
                    convertedArgs.Clear();
                    convertedArgs.Add("version");
                    break;

                case "--help":
                case "-h":
                case "-?":
                    convertedArgs.Add("--help");
                    break;

                default:
                    // Pass through any unrecognized arguments
                    if (!arg.StartsWith('-'))
                    {
                        convertedArgs.Add(arg);
                    }
                    break;
            }
        }

        // If MCP_TRANSPORT environment variable is set to SSE, enable web mode
        if (
            Environment.GetEnvironmentVariable("MCP_TRANSPORT") == "sse"
            && !convertedArgs.Contains("--web")
        )
        {
            convertedArgs.Add("--web");
        }

        return convertedArgs.ToArray();
    }
}
