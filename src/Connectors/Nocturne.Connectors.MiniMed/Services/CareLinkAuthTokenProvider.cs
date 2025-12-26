using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;

namespace Nocturne.Connectors.MiniMed.Services;

/// <summary>
/// Token provider for MiniMed CareLink authentication.
/// Handles the complex multi-step form-based authentication flow.
/// </summary>
public class CareLinkAuthTokenProvider : AuthTokenProviderBase<CareLinkConnectorConfiguration>
{
    private readonly IRetryDelayStrategy _retryDelayStrategy;

    public CareLinkAuthTokenProvider(
        IOptions<CareLinkConnectorConfiguration> config,
        HttpClient httpClient,
        ILogger<CareLinkAuthTokenProvider> logger,
        IRetryDelayStrategy retryDelayStrategy)
        : base(config.Value, httpClient, logger)
    {
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
    }

    /// <summary>
    /// CareLink sessions typically last 4 hours.
    /// </summary>
    protected override int TokenLifetimeBufferMinutes => 30;

    protected override async Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        const int maxRetries = 3;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Authenticating with MiniMed CareLink for user: {Username} (attempt {Attempt}/{MaxRetries})",
                    _config.CareLinkUsername,
                    attempt + 1,
                    maxRetries);

                // Step 1: Get login flow
                var loginFlow = await GetLoginFlowAsync(cancellationToken);
                if (loginFlow == null)
                {
                    if (attempt < maxRetries - 1)
                    {
                        await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                        continue;
                    }
                    return (null, DateTime.MinValue);
                }

                // Step 2: Submit credentials
                var authResult = await SubmitLoginAsync(loginFlow, cancellationToken);
                if (authResult == null)
                {
                    if (attempt < maxRetries - 1)
                    {
                        await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                        continue;
                    }
                    return (null, DateTime.MinValue);
                }

                // Step 3: Handle consent step and get token
                var token = await HandleConsentAsync(authResult, cancellationToken);
                if (string.IsNullOrEmpty(token))
                {
                    if (attempt < maxRetries - 1)
                    {
                        await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                        continue;
                    }
                    return (null, DateTime.MinValue);
                }

                var expiresAt = DateTime.UtcNow.AddHours(4);
                _logger.LogInformation("MiniMed CareLink authentication successful");
                return (token, expiresAt);
            }
            catch (HttpRequestException ex) when (IsRetryableError(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Retryable error during CareLink authentication (attempt {Attempt}/{MaxRetries}): {Message}",
                    attempt + 1,
                    maxRetries,
                    ex.Message);

                if (attempt < maxRetries - 1)
                {
                    await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                }
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning(
                    ex,
                    "Timeout during CareLink authentication (attempt {Attempt}/{MaxRetries})",
                    attempt + 1,
                    maxRetries);

                if (attempt < maxRetries - 1)
                {
                    await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Non-retryable error during MiniMed CareLink authentication: {Message}",
                    ex.Message);
                return (null, DateTime.MinValue);
            }
        }

        _logger.LogError("Failed to authenticate with MiniMed CareLink after {MaxRetries} attempts", maxRetries);
        return (null, DateTime.MinValue);
    }

    private static bool IsRetryableError(Exception ex)
    {
        return ex switch
        {
            HttpRequestException httpEx => httpEx.Message.Contains("timeout")
                || httpEx.Message.Contains("network")
                || httpEx.Message.Contains("connection"),
            TaskCanceledException => true,
            TimeoutException => true,
            _ => false,
        };
    }

    private async Task<CarelinkLoginFlow?> GetLoginFlowAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = $"/patient/sso/login?country={_config.CareLinkCountry}&lang=en";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get CareLink login flow: {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            // Extract form data using regex
            var endpointMatch = Regex.Match(content, @"<form action=""(.*)"" method=""POST""");
            var sessionIdMatch = Regex.Match(content, @"<input type=""hidden"" name=""sessionID"" value=""(.*)""");
            var sessionDataMatch = Regex.Match(content, @"<input type=""hidden"" name=""sessionData"" value=""(.*)""");

            if (!endpointMatch.Success || !sessionIdMatch.Success || !sessionDataMatch.Success)
            {
                _logger.LogError("Failed to parse CareLink login flow from response");
                return null;
            }

            return new CarelinkLoginFlow
            {
                Endpoint = endpointMatch.Groups[1].Value,
                SessionId = sessionIdMatch.Groups[1].Value,
                SessionData = sessionDataMatch.Groups[1].Value,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting CareLink login flow");
            return null;
        }
    }

    private async Task<CarelinkAuthResult?> SubmitLoginAsync(CarelinkLoginFlow loginFlow, CancellationToken cancellationToken)
    {
        try
        {
            var payload = new Dictionary<string, string>
            {
                { "sessionID", loginFlow.SessionId },
                { "sessionData", loginFlow.SessionData },
                { "locale", _config.CareLinkCountry },
                { "action", "login" },
                { "username", _config.CareLinkUsername },
                { "password", _config.CareLinkPassword },
            };

            var formContent = new FormUrlEncodedContent(payload);
            var response = await _httpClient.PostAsync(loginFlow.Endpoint, formContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to submit CareLink login: {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            // Check for authentication failure
            if (content.Contains("error") || content.Contains("invalid"))
            {
                _logger.LogError("Invalid CareLink credentials");
                return null;
            }

            // Extract consent form data
            var endpointMatch = Regex.Match(content, @"<form action=""(.*)"" method=""POST""");
            var sessionIdMatch = Regex.Match(content, @"<input type=""hidden"" name=""sessionID"" value=""(.*)""");
            var sessionDataMatch = Regex.Match(content, @"<input type=""hidden"" name=""sessionData"" value=""(.*)""");

            if (!endpointMatch.Success)
            {
                _logger.LogError("Failed to parse CareLink consent flow from response");
                return null;
            }

            return new CarelinkAuthResult
            {
                ConsentEndpoint = endpointMatch.Groups[1].Value,
                SessionId = sessionIdMatch.Success ? sessionIdMatch.Groups[1].Value : loginFlow.SessionId,
                SessionData = sessionDataMatch.Success ? sessionDataMatch.Groups[1].Value : loginFlow.SessionData,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting CareLink login");
            return null;
        }
    }

    private async Task<string?> HandleConsentAsync(CarelinkAuthResult authResult, CancellationToken cancellationToken)
    {
        try
        {
            var payload = new Dictionary<string, string>
            {
                { "sessionID", authResult.SessionId },
                { "sessionData", authResult.SessionData },
                { "action", "consent" },
                { "response_type", "code" },
                { "response_mode", "query" },
            };

            var formContent = new FormUrlEncodedContent(payload);
            var response = await _httpClient.PostAsync(authResult.ConsentEndpoint, formContent, cancellationToken);

            // The response should redirect and contain authorization tokens
            var location = response.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(location))
            {
                _logger.LogError("No redirect location in CareLink consent response");
                return null;
            }

            // Extract token from cookies
            if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                _logger.LogError("No cookies in CareLink consent response");
                return null;
            }

            var authCookie = cookies.FirstOrDefault(c => c.Contains("auth_tmp_token"));
            if (string.IsNullOrEmpty(authCookie))
            {
                _logger.LogError("No auth token found in CareLink consent response");
                return null;
            }

            // Extract token value
            var tokenMatch = Regex.Match(authCookie, @"auth_tmp_token=([^;]+)");
            return tokenMatch.Success ? tokenMatch.Groups[1].Value : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling CareLink consent");
            return null;
        }
    }

    #region Internal Models

    private class CarelinkLoginFlow
    {
        public required string Endpoint { get; init; }
        public required string SessionId { get; init; }
        public required string SessionData { get; init; }
    }

    private class CarelinkAuthResult
    {
        public required string ConsentEndpoint { get; init; }
        public required string SessionId { get; init; }
        public required string SessionData { get; init; }
    }

    #endregion
}
