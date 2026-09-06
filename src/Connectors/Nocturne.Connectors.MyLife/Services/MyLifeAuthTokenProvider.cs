using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
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
    ILogger<MyLifeAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<MyLifeConnectorConfiguration>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
{
    private readonly IMyLifeSessionCache _sessionCache = sessionCache;
    private readonly MyLifeSoapClient _soapClient = soapClient;

    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    protected override int TokenLifetimeBufferMinutes => 60;

    protected override string ConnectorName => "MyLife";

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        MyLifeConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var maxRetries = LoginAttempts(config);

        var authToken = await ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with MyLife (attempt {Attempt}/{MaxRetries})", attempt + 1, maxRetries);

                return await SignInAsync(config, cancellationToken);
            },
            _retryDelayStrategy,
            maxRetries,
            "MyLife authentication",
            cancellationToken);

        if (string.IsNullOrEmpty(authToken))
            return (null, DateTime.MinValue, null);

        return (authToken, DateTime.UtcNow.AddHours(24), null);
    }

    /// <summary>
    ///     Performs one MyLife sign-in attempt and caches the resulting session, returning the auth
    ///     token or null plus whether the failure is worth another attempt.
    /// </summary>
    /// <remarks>
    ///     Only a failed status or a transport failure can buy another attempt. Every non-2xx leaves
    ///     <see cref="MyLifeSoapClient"/> as an <see cref="HttpRequestException"/> carrying the status,
    ///     which is classified by the shared
    ///     <see cref="HttpResponseExtensions.IsRetryableStatusCode"/>; a transport failure carries no
    ///     status and reaches the base class, which retries it.
    ///     <para>
    ///     Everything else here is a 2xx that did not carry what the next step needs — an unknown
    ///     username, a login MyLife declined, an account with no matching patient. Asking again
    ///     produces the same answer, so none of them retries. MyLife declines a login by returning a
    ///     <see cref="MyLifeLoginResult"/> with no <see cref="MyLifeLoginResult.AuthToken"/> rather
    ///     than by failing the request.
    ///     </para>
    ///     <para>
    ///     Every step logs which one failed, because a null token surfaces downstream only as the
    ///     generic "MyLife authentication failed". Messages are intentionally free of credentials/PII.
    ///     </para>
    /// </remarks>
    private async Task<(string? Token, bool ShouldRetry)> SignInAsync(
        MyLifeConnectorConfiguration config, CancellationToken cancellationToken)
    {
        try
        {
            var location = await _soapClient.GetUserLocationAsync(
                config.Username,
                cancellationToken
            );
            if (location == null)
            {
                _logger.LogWarning("MyLife auth failed at user-location lookup (GetUser20 returned no result)");
                return (null, false);
            }

            var serviceUrl = config.ServiceUrl;
            if (string.IsNullOrWhiteSpace(serviceUrl))
                serviceUrl = location.Country20?.ServiceUrl ?? location.Country20?.RestServiceUrl ?? string.Empty;

            if (string.IsNullOrWhiteSpace(serviceUrl))
            {
                _logger.LogWarning("MyLife auth failed: no service URL resolved from user location");
                return (null, false);
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
                return (null, false);
            }

            if (string.IsNullOrWhiteSpace(login.AuthToken))
            {
                _logger.LogWarning(
                    "MyLife auth failed: login returned no auth token (check credentials; appVersion {AppVersion})",
                    config.AppVersion);
                return (null, false);
            }

            var patients = await _soapClient.SyncPatientListAsync(
                serviceUrl,
                login.AuthToken,
                cancellationToken
            );
            if (patients.Count == 0)
            {
                _logger.LogWarning("MyLife auth failed: patient list was empty");
                return (null, false);
            }

            var patient = ResolvePatient(patients, config.PatientId);
            if (patient == null)
            {
                _logger.LogWarning(
                    "MyLife auth failed: configured patient not found among {Count} patient(s)",
                    patients.Count);
                return (null, false);
            }

            var restServiceUrl = location.Country20?.RestServiceUrl ?? string.Empty;

            _sessionCache.Set(_tenantAccessor.TenantId, new MyLifeSession(
                serviceUrl,
                restServiceUrl,
                login.AuthToken,
                login.UserId ?? string.Empty,
                patient.OnlinePatientId ?? string.Empty
            ));

            return (login.AuthToken, false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status && !HttpResponseExtensions.IsRetryableStatusCode(status))
        {
            _logger.LogError("MyLife auth failed with non-retryable HTTP {StatusCode}", (int)status);
            return (null, false);
        }
        catch (InvalidOperationException ex)
        {
            // A member-supplied ServiceUrl the SOAP client refuses to send to; no request was made
            // and the next attempt would build the same URL.
            _logger.LogError(ex, "MyLife auth failed: service URL is unusable");
            return (null, false);
        }
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
