using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeAuthTokenProvider(
    HttpClient httpClient,
    IConnectorTokenCache tokenCache,
    IConnectorServerResolver<MyLifeConnectorConfiguration> serverResolver,
    ITenantAccessor tenantAccessor,
    MyLifeSoapClient soapClient,
    IMyLifeSessionCache sessionCache,
    ILogger<MyLifeAuthTokenProvider> logger)
    : AuthTokenProviderBase<MyLifeConnectorConfiguration>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
{
    private readonly IMyLifeSessionCache _sessionCache = sessionCache;
    private readonly MyLifeSoapClient _soapClient = soapClient;

    protected override int TokenLifetimeBufferMinutes => 60;

    protected override string ConnectorName => "MyLife";

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        MyLifeConnectorConfiguration config, CancellationToken cancellationToken)
    {
        // Each step below fails by returning a null token (the base class records the connector as
        // unhealthy). Log which step failed so the cause is diagnosable — without this every failure
        // surfaces only as the generic "MyLife authentication failed" downstream. Messages are
        // intentionally free of credentials/PII.
        var location = await _soapClient.GetUserLocationAsync(
            config.Username,
            cancellationToken
        );
        if (location == null)
        {
            _logger.LogWarning("MyLife auth failed at user-location lookup (GetUser20 returned no result)");
            return (null, DateTime.MinValue, null);
        }

        var serviceUrl = config.ServiceUrl;
        if (string.IsNullOrWhiteSpace(serviceUrl))
            serviceUrl = location.Country20?.ServiceUrl ?? location.Country20?.RestServiceUrl ?? string.Empty;

        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            _logger.LogWarning("MyLife auth failed: no service URL resolved from user location");
            return (null, DateTime.MinValue, null);
        }

        var login = await _soapClient.LoginAsync(
            serviceUrl,
            config.AppPlatform,
            config.AppVersion,
            config.Username,
            config.Password,
            cancellationToken
        );
        if (login == null)
        {
            _logger.LogWarning(
                "MyLife auth failed at login: no response (appVersion {AppVersion}, appPlatform {AppPlatform})",
                config.AppVersion, config.AppPlatform);
            return (null, DateTime.MinValue, null);
        }

        if (string.IsNullOrWhiteSpace(login.AuthToken))
        {
            _logger.LogWarning(
                "MyLife auth failed: login returned no auth token (check credentials; appVersion {AppVersion})",
                config.AppVersion);
            return (null, DateTime.MinValue, null);
        }

        var patients = await _soapClient.SyncPatientListAsync(
            serviceUrl,
            login.AuthToken,
            cancellationToken
        );
        if (patients.Count == 0)
        {
            _logger.LogWarning("MyLife auth failed: patient list was empty");
            return (null, DateTime.MinValue, null);
        }

        var patient = ResolvePatient(patients, config.PatientId);
        if (patient == null)
        {
            _logger.LogWarning(
                "MyLife auth failed: configured patient not found among {Count} patient(s)",
                patients.Count);
            return (null, DateTime.MinValue, null);
        }

        var restServiceUrl = location.Country20?.RestServiceUrl ?? string.Empty;

        _sessionCache.Set(_tenantAccessor.TenantId, new MyLifeSession(
            serviceUrl,
            restServiceUrl,
            login.AuthToken,
            login.UserId ?? string.Empty,
            patient.OnlinePatientId ?? string.Empty
        ));

        var expiresAt = DateTime.UtcNow.AddHours(24);
        return (login.AuthToken, expiresAt, null);
    }

    private static MyLifePatient? ResolvePatient(
        IReadOnlyList<MyLifePatient> patients,
        string configuredPatientId)
    {
        if (string.IsNullOrWhiteSpace(configuredPatientId))
            return patients.FirstOrDefault();

        // Match by OnlinePatientId first, then fall back to email
        return patients.FirstOrDefault(p => p.OnlinePatientId == configuredPatientId)
            ?? patients.FirstOrDefault(p =>
                string.Equals(p.Email, configuredPatientId, StringComparison.OrdinalIgnoreCase));
    }
}
