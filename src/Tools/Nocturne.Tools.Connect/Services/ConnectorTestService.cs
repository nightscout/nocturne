using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Services;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Tools.Abstractions.Services;
using Nocturne.Tools.Connect.Configuration;

namespace Nocturne.Tools.Connect.Services;

/// <summary>
/// Service for testing actual connector connections using the real connector implementations
/// </summary>
public class ConnectorTestService
{
    private readonly ILogger<ConnectorTestService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public ConnectorTestService(ILogger<ConnectorTestService> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <summary>
    /// Tests Glooko connection using actual authentication
    /// </summary>
    public async Task<ConnectionTestResult> TestGlookoConnectionAsync(
        ConnectConfiguration config,
        CancellationToken cancellationToken = default
    )
    {
        var startTime = DateTime.UtcNow;

        try
        {
            if (
                string.IsNullOrWhiteSpace(config.GlookoEmail)
                || string.IsNullOrWhiteSpace(config.GlookoPassword)
            )
            {
                return new ConnectionTestResult(
                    false,
                    "Glooko credentials not configured",
                    DateTime.UtcNow - startTime
                );
            }

            var glookoConfig = new GlookoConnectorConfiguration
            {
                ConnectSource = ConnectSource.Glooko,
                Email = config.GlookoEmail,
                Password = config.GlookoPassword,
                Server = config.GlookoServer,
            };

            var httpClient = new HttpClient();
            var tokenProvider = new GlookoAuthTokenProvider(
                Options.Create(glookoConfig),
                httpClient,
                _loggerFactory.CreateLogger<GlookoAuthTokenProvider>()
            );
            using var connector = new GlookoConnectorService(
                httpClient,
                Options.Create(glookoConfig),
                _loggerFactory.CreateLogger<GlookoConnectorService>(),
                new ProductionRetryDelayStrategy(),
                new ProductionRateLimitingStrategy(_loggerFactory.CreateLogger<ProductionRateLimitingStrategy>()),
                tokenProvider,
                new TreatmentClassificationService()
            );

            _logger.LogInformation(
                "Testing Glooko authentication with server: {Server}",
                config.GlookoServer
            );

            var authResult = await connector.AuthenticateAsync();
            var duration = DateTime.UtcNow - startTime;

            if (authResult)
            {
                return new ConnectionTestResult(
                    true,
                    $"Successfully authenticated with Glooko server {config.GlookoServer}",
                    duration
                );
            }
            else
            {
                return new ConnectionTestResult(
                    false,
                    "Glooko authentication failed - check credentials and server configuration",
                    duration
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing Glooko connection");
            return new ConnectionTestResult(
                false,
                $"Glooko connection test failed: {ex.Message}",
                DateTime.UtcNow - startTime
            );
        }
    }

    /// <summary>
    /// Tests Dexcom Share connection using actual authentication
    /// </summary>
    public async Task<ConnectionTestResult> TestDexcomConnectionAsync(
        ConnectConfiguration config,
        CancellationToken cancellationToken = default
    )
    {
        var startTime = DateTime.UtcNow;

        try
        {
            if (
                string.IsNullOrWhiteSpace(config.DexcomUsername)
                || string.IsNullOrWhiteSpace(config.DexcomPassword)
            )
            {
                return new ConnectionTestResult(
                    false,
                    "Dexcom credentials not configured",
                    DateTime.UtcNow - startTime
                );
            }

            var dexcomConfig = new DexcomConnectorConfiguration
            {
                ConnectSource = ConnectSource.Dexcom,
                Username = config.DexcomUsername,
                Password = config.DexcomPassword,
                Server = config.DexcomRegion,
            };

            var dexcomHttpClient = new HttpClient();
            var dexcomTokenProvider = new DexcomAuthTokenProvider(
                Options.Create(dexcomConfig),
                dexcomHttpClient,
                _loggerFactory.CreateLogger<DexcomAuthTokenProvider>(),
                new ProductionRetryDelayStrategy()
            );
            using var connector = new DexcomConnectorService(
                dexcomHttpClient,
                _loggerFactory.CreateLogger<DexcomConnectorService>(),
                new ProductionRetryDelayStrategy(),
                new ProductionRateLimitingStrategy(_loggerFactory.CreateLogger<ProductionRateLimitingStrategy>()),
                dexcomTokenProvider
            );

            _logger.LogInformation(
                "Testing Dexcom Share authentication with region: {Region}",
                config.DexcomRegion
            );

            var authResult = await connector.AuthenticateAsync();
            var duration = DateTime.UtcNow - startTime;

            if (authResult)
            {
                return new ConnectionTestResult(
                    true,
                    $"Successfully authenticated with Dexcom Share region {config.DexcomRegion}",
                    duration
                );
            }
            else
            {
                return new ConnectionTestResult(
                    false,
                    "Dexcom Share authentication failed - check credentials and region configuration",
                    duration
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing Dexcom connection");
            return new ConnectionTestResult(
                false,
                $"Dexcom connection test failed: {ex.Message}",
                DateTime.UtcNow - startTime
            );
        }
    }

    /// <summary>
    /// Tests LibreLinkUp connection using actual authentication
    /// </summary>
    public async Task<ConnectionTestResult> TestLibreLinkUpConnectionAsync(
        ConnectConfiguration config,
        CancellationToken cancellationToken = default
    )
    {
        var startTime = DateTime.UtcNow;

        try
        {
            if (
                string.IsNullOrWhiteSpace(config.LibreUsername)
                || string.IsNullOrWhiteSpace(config.LibrePassword)
            )
            {
                return new ConnectionTestResult(
                    false,
                    "LibreLinkUp credentials not configured",
                    DateTime.UtcNow - startTime
                );
            }

            var libreConfig = new LibreLinkUpConnectorConfiguration
            {
                ConnectSource = ConnectSource.LibreLinkUp,
                Username = config.LibreUsername,
                Password = config.LibrePassword,
                Region = config.LibreRegion,
            };

            var libreHttpClient = new HttpClient();
            var libreTokenProvider = new LibreLinkAuthTokenProvider(
                Options.Create(libreConfig),
                libreHttpClient,
                _loggerFactory.CreateLogger<LibreLinkAuthTokenProvider>(),
                new ProductionRetryDelayStrategy()
            );
            using var connector = new LibreConnectorService(
                libreHttpClient,
                Options.Create(libreConfig),
                _loggerFactory.CreateLogger<LibreConnectorService>(),
                new ProductionRetryDelayStrategy(),
                new ProductionRateLimitingStrategy(_loggerFactory.CreateLogger<ProductionRateLimitingStrategy>()),
                libreTokenProvider
            );

            _logger.LogInformation(
                "Testing LibreLinkUp authentication with region: {Region}",
                config.LibreRegion
            );

            var authResult = await connector.AuthenticateAsync();
            var duration = DateTime.UtcNow - startTime;

            if (authResult)
            {
                return new ConnectionTestResult(
                    true,
                    $"Successfully authenticated with LibreLinkUp region {config.LibreRegion}",
                    duration
                );
            }
            else
            {
                return new ConnectionTestResult(
                    false,
                    "LibreLinkUp authentication failed - check credentials and region configuration",
                    duration
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing LibreLinkUp connection");
            return new ConnectionTestResult(
                false,
                $"LibreLinkUp connection test failed: {ex.Message}",
                DateTime.UtcNow - startTime
            );
        }
    }
}
