using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Models;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeAuthTokenProvider(
    IOptions<MyLifeConnectorConfiguration> config,
    HttpClient httpClient,
    MyLifeSoapClient soapClient,
    MyLifeSessionStore sessionStore,
    ILogger<MyLifeAuthTokenProvider> logger)
    : AuthTokenProviderBase<MyLifeConnectorConfiguration>(config.Value, httpClient, logger)
{
    private readonly MyLifeSessionStore _sessionStore = sessionStore;
    private readonly MyLifeSoapClient _soapClient = soapClient;

    protected override int TokenLifetimeBufferMinutes => 60;

    protected override async Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(
        CancellationToken cancellationToken)
    {
        var location = await _soapClient.GetUserLocationAsync(
            _config.Username,
            cancellationToken
        );
        if (location == null) return (null, DateTime.MinValue);

        var serviceUrl = _config.ServiceUrl;
        if (string.IsNullOrWhiteSpace(serviceUrl))
            serviceUrl = location.Country20?.ServiceUrl ?? location.Country20?.RestServiceUrl ?? string.Empty;

        if (string.IsNullOrWhiteSpace(serviceUrl)) return (null, DateTime.MinValue);

        var login = await _soapClient.LoginAsync(
            serviceUrl,
            _config.AppPlatform,
            _config.AppVersion,
            _config.Username,
            _config.Password,
            cancellationToken
        );
        if (login == null) return (null, DateTime.MinValue);

        if (string.IsNullOrWhiteSpace(login.AuthToken)) return (null, DateTime.MinValue);

        var patients = await _soapClient.SyncPatientListAsync(
            serviceUrl,
            login.AuthToken,
            cancellationToken
        );
        if (patients.Count == 0) return (null, DateTime.MinValue);

        var patient = ResolvePatient(patients, _config.PatientId);
        if (patient == null) return (null, DateTime.MinValue);

        _sessionStore.SetSession(
            serviceUrl,
            login.AuthToken,
            login.UserId ?? string.Empty,
            patient.OnlinePatientId ?? string.Empty
        );

        var expiresAt = DateTime.UtcNow.AddHours(24);
        return (login.AuthToken, expiresAt);
    }

    private static MyLifePatient? ResolvePatient(
        IReadOnlyList<MyLifePatient> patients,
        string configuredPatientId)
    {
        if (!string.IsNullOrWhiteSpace(configuredPatientId))
            return patients.FirstOrDefault(p => p.OnlinePatientId == configuredPatientId);

        return patients.FirstOrDefault();
    }
}