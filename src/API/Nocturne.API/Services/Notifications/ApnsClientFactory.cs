using dotAPNS;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Notifications;

/// <summary>
/// Production implementation of <see cref="IApnsClientFactory"/> that creates JWT-authenticated
/// APNs clients via the <c>dotAPNS</c> library.
/// </summary>
/// <remarks>
/// JWT authentication requires all three of <see cref="LoopConfiguration.ApnsKey"/>,
/// <see cref="LoopConfiguration.ApnsKeyId"/>, and <see cref="LoopConfiguration.DeveloperTeamId"/>
/// to be non-empty, and the team ID must be exactly 10 characters (Apple's format).
/// </remarks>
/// <seealso cref="IApnsClientFactory"/>
public class ApnsClientFactory : IApnsClientFactory
{
    private readonly LoopConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ApnsClientFactory> _logger;

    public ApnsClientFactory(
        IOptions<LoopConfiguration> configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<ApnsClientFactory> logger
    )
    {
        _configuration = configuration.Value;
        _httpClient = httpClientFactory.CreateClient("dotAPNS");
        _logger = logger;
    }

    /// <summary>
    /// Checks if the factory is properly configured with all required settings
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_configuration.ApnsKey)
        && !string.IsNullOrEmpty(_configuration.ApnsKeyId)
        && !string.IsNullOrEmpty(_configuration.DeveloperTeamId)
        && _configuration.DeveloperTeamId.Length == 10;

    /// <summary>
    /// Creates an APNs client configured for the specified bundle ID.
    /// Returns <see langword="null"/> when <see cref="IsConfigured"/> is <see langword="false"/>
    /// or when the dotAPNS constructor throws (e.g. malformed PEM key).
    /// </summary>
    public IApnsClient? CreateClient(string bundleId)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Cannot create APNS client: configuration is invalid");
            return null;
        }

        try
        {
            var options = new ApnsJwtOptions
            {
                KeyId = _configuration.ApnsKeyId!,
                TeamId = _configuration.DeveloperTeamId!,
                CertContent = _configuration.ApnsKey!,
                BundleId = bundleId,
            };

            return ApnsClient.CreateUsingJwt(_httpClient, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create APNS client for bundle: {BundleId}", bundleId);
            return null;
        }
    }
}
