using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.WebUtilities;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Models;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Health;

namespace Nocturne.API.Services.Health.GoogleHealth;

public sealed class GoogleHealthCoordinator : IGoogleHealthSyncCoordinator
{
    internal sealed record Flow(
        string State,
        string Verifier,
        Guid SubjectId,
        string Settings,
        DateTimeOffset Expires);

    internal sealed record SyncProgress(
        GoogleHealthSyncPhase Phase,
        string? DataType,
        int CompletedDataTypes,
        int TotalDataTypes,
        int PagesRead);

    private readonly Channel<Guid> syncRequests = Channel.CreateUnbounded<Guid>(new()
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<Guid, SyncProgress> syncProgress = new();
    internal ConcurrentDictionary<Guid, Flow> Flows { get; } = new();
    internal ConcurrentDictionary<Guid, SemaphoreSlim> Locks { get; } = new();

    public SemaphoreSlim Gate(Guid tenantId) =>
        Locks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1));

    internal bool Queue(Guid tenantId, int totalDataTypes)
    {
        if (!syncProgress.TryAdd(
                tenantId,
                new SyncProgress(GoogleHealthSyncPhase.Queued, null, 0, totalDataTypes, 0)))
            return false;
        if (syncRequests.Writer.TryWrite(tenantId)) return true;
        syncProgress.TryRemove(tenantId, out _);
        return false;
    }

    internal IAsyncEnumerable<Guid> ReadRequestsAsync(CancellationToken ct) =>
        syncRequests.Reader.ReadAllAsync(ct);

    internal bool StartQueued(Guid tenantId) => Update(tenantId, current =>
        current.Phase == GoogleHealthSyncPhase.Queued
            ? current with { Phase = GoogleHealthSyncPhase.Preparing }
            : null);

    public void Report(
        Guid tenantId,
        GoogleHealthSyncPhase phase,
        string? dataType = null,
        int? completedDataTypes = null,
        int? totalDataTypes = null,
        int? pagesRead = null) => Update(tenantId, current => current with
    {
        Phase = phase,
        DataType = dataType,
        CompletedDataTypes = completedDataTypes ?? current.CompletedDataTypes,
        TotalDataTypes = totalDataTypes ?? current.TotalDataTypes,
        PagesRead = pagesRead ?? current.PagesRead
    });

    internal SyncProgress? Progress(Guid tenantId) =>
        syncProgress.TryGetValue(tenantId, out var progress) ? progress : null;

    internal void Complete(Guid tenantId) => syncProgress.TryRemove(tenantId, out _);

    private bool Update(Guid tenantId, Func<SyncProgress, SyncProgress?> update)
    {
        while (syncProgress.TryGetValue(tenantId, out var current))
        {
            var next = update(current);
            if (next is null) return false;
            if (syncProgress.TryUpdate(tenantId, next, current)) return true;
        }
        return false;
    }
}

public sealed class GoogleHealthService(
    GoogleHealthCoordinator coordinator,
    GoogleHealthClient google,
    GoogleHealthAuthTokenProvider oauth,
    IConnectorConfigurationService connectorConfigurations,
    IConnectorConfigurationLoader<GoogleHealthConnectorConfiguration> configurationLoader,
    ITenantAccessor tenantAccessor,
    IGoogleHealthReadingWriter? writer = null,
    ILogger<GoogleHealthService>? logger = null) : IGoogleHealthService
{
    private const string ConnectorName = "GoogleHealth";
    private const string AccountKeySecret = "accountKey";
    private static readonly TimeSpan AccessTokenSafety = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Guid TenantId => tenantAccessor.TenantId;

    private async Task<GoogleHealthOptions?> StoredOptionsOrNullAsync(CancellationToken ct)
    {
        var stored = await connectorConfigurations.GetConfigurationAsync(ConnectorName, ct);
        if (stored is null || !stored.IsActive)
            return null;

        var configuration = await configurationLoader.LoadForTenantAsync(ct);
        DateTimeOffset? importFrom = null;
        if (!string.IsNullOrWhiteSpace(configuration.ImportFrom))
            importFrom = DateTimeOffset.Parse(
                configuration.ImportFrom,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

        return new GoogleHealthOptions
        {
            ClientId = configuration.ClientId,
            ClientSecret = configuration.ClientSecret,
            CallbackUrl = configuration.CallbackUrl,
            DataTypes = SelectedTypes(configuration),
            HistoryDays = configuration.HistoryDays,
            ImportFrom = importFrom,
            PreviewOnly = configuration.PreviewOnly
        };
    }

    private async Task<GoogleHealthOptions> StoredOptionsAsync(CancellationToken ct) =>
        await StoredOptionsOrNullAsync(ct) ?? throw new GoogleHealthException("configure_first");

    private async Task<GoogleHealthTokenSession?> StoredSessionAsync(CancellationToken ct)
    {
        var configuration = await configurationLoader.LoadForTenantAsync(ct);
        if (!configuration.Enabled || string.IsNullOrWhiteSpace(configuration.RefreshToken))
            return null;

        try
        {
            var cached = await oauth.GetCurrentSessionAsync();
            if (cached is not null &&
                string.Equals(cached.RefreshToken, configuration.RefreshToken, StringComparison.Ordinal))
                return cached;
        }
        catch (GoogleHealthException)
        {
            oauth.InvalidateToken();
        }

        var scopes = (configuration.GrantedScopes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new GoogleHealthTokenSession(configuration.RefreshToken, scopes);
    }

    private async Task SaveOptionsAsync(GoogleHealthOptions options, Guid subject, CancellationToken ct)
    {
        var stored = await connectorConfigurations.GetConfigurationAsync(ConnectorName, ct);
        var configuration = stored?.Configuration.RootElement.Deserialize<JsonObject>(Json) ?? new JsonObject();
        configuration["enabled"] ??= true;
        configuration["clientId"] = options.ClientId;
        configuration["callbackUrl"] = options.CallbackUrl;
        configuration["lookbackDays"] = options.HistoryDays;
        configuration["importFrom"] = options.ImportFrom?.ToString("O", CultureInfo.InvariantCulture);
        configuration["previewOnly"] = options.PreviewOnly;
        configuration["syncSteps"] = options.DataTypes.Contains("steps", StringComparer.Ordinal);
        configuration["syncHeartRate"] = options.DataTypes.Contains("heart-rate", StringComparer.Ordinal);
        configuration["syncBodyWeight"] = options.DataTypes.Contains("weight", StringComparer.Ordinal);
        configuration["syncSleep"] = options.DataTypes.Contains("sleep", StringComparer.Ordinal);
        using var document = JsonDocument.Parse(configuration.ToJsonString(Json));
        await connectorConfigurations.SaveConfigurationAsync(
            ConnectorName, document, subject.ToString(), ct);

        var secrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
        secrets["clientSecret"] = options.ClientSecret!;
        await connectorConfigurations.SaveSecretsAsync(
            ConnectorName, secrets, subject.ToString(), ct);
    }

    private async Task SaveSessionAsync(
        GoogleHealthOptions settings,
        GoogleHealthTokenSession token,
        string accountKey,
        Guid subject,
        CancellationToken ct)
    {
        var secrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
        secrets["clientSecret"] = settings.ClientSecret!;
        secrets["refreshToken"] = token.RefreshToken;
        secrets["grantedScopes"] = string.Join(' ', token.Scopes.Distinct(StringComparer.Ordinal));
        secrets[AccountKeySecret] = accountKey;
        await connectorConfigurations.SaveSecretsAsync(
            ConnectorName, secrets, subject.ToString(), ct);
    }

    private async Task RemoveSessionAsync(Guid subject, bool removeAccount, CancellationToken ct)
    {
        var secrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
        secrets.Remove("refreshToken");
        secrets.Remove("grantedScopes");
        if (removeAccount) secrets.Remove(AccountKeySecret);
        await connectorConfigurations.SaveSecretsAsync(
            ConnectorName, secrets, subject.ToString(), ct);
    }

    private async Task<GoogleHealthTokenSession> RefreshSessionAsync(
        GoogleHealthOptions settings,
        GoogleHealthTokenSession token,
        CancellationToken ct)
    {
        await oauth.SeedSessionAsync(token);
        var accessToken = await oauth.GetValidTokenAsync(Configuration(settings, token.RefreshToken), ct);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new GoogleHealthException("invalid_token_response", stage: "token_refresh");
        return await oauth.GetCurrentSessionAsync() ??
            throw new GoogleHealthException("invalid_token_response", stage: "token_cache");
    }

    public async Task<GoogleHealthStatus> StatusAsync(CancellationToken ct)
    {
        try
        {
            var settings = await StoredOptionsOrNullAsync(ct);
            if (settings is null)
                return WithProgress(new GoogleHealthStatus { Capabilities = GoogleHealthClient.Capabilities });

            var stored = await connectorConfigurations.GetConfigurationAsync(ConnectorName, ct);
            var session = await StoredSessionAsync(ct);
            var selected = settings.DataTypes
                .Where(type => GoogleHealthClient.SupportedTypes.Contains(type, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var storedError = GoogleHealthErrorCode.Decode(stored?.LastErrorMessage);
            var missingScopes = selected
                .Where(type => session is not null &&
                    !session.Scopes.Contains(GoogleHealthClient.ScopeFor(type), StringComparer.Ordinal))
                .ToArray();

            return WithProgress(new GoogleHealthStatus
            {
                Capabilities = GoogleHealthClient.Capabilities,
                Configured = true,
                Connected = session is not null,
                ClientId = settings.ClientId,
                CallbackUrl = settings.CallbackUrl,
                SelectedTypes = selected,
                GrantedTypes = GoogleHealthClient.SupportedTypes
                    .Where(type => session?.Scopes.Contains(GoogleHealthClient.ScopeFor(type), StringComparer.Ordinal) == true)
                    .ToArray(),
                HistoryDays = settings.HistoryDays,
                ImportFrom = settings.ImportFrom,
                AccessTokenExpiresAt = session?.AccessTokenExpiresAt,
                LastAttempt = AsOffset(stored?.LastSyncAttempt),
                LastSync = AsOffset(stored?.LastSuccessfulSync),
                ErrorCode = selected.Length != settings.DataTypes.Length
                    ? "unsupported_type"
                    : missingScopes.Length > 0 ? "partial_consent" : storedError.Code,
                ErrorDataTypes = missingScopes.Length > 0 ? missingScopes : storedError.DataTypes,
                PreviewRequired = settings.PreviewOnly
            });
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            logger?.LogWarning(ex,
                "Google Health configuration could not be read for tenant {TenantId}", TenantId);
            return WithProgress(new GoogleHealthStatus
            {
                Capabilities = GoogleHealthClient.Capabilities,
                Configured = true,
                ErrorCode = "stored_google_configuration_unreadable"
            });
        }
    }

    public static void ValidateOptions(GoogleHealthOptions options)
    {
        if (options.DataTypes is null || options.DataTypes.Length > 32 ||
            options.DataTypes.Distinct().Count() != options.DataTypes.Length ||
            options.DataTypes.Except(GoogleHealthClient.SupportedTypes).Any())
            throw new GoogleHealthException("unsupported_type");
        if (!GoogleHealthConnectorConfiguration.IsValidClientId(options.ClientId) ||
            options.HistoryDays is < 1 or > 90 ||
            options.ImportFrom is { } importFrom &&
            (importFrom < new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero) ||
             importFrom > DateTimeOffset.UtcNow.AddDays(1)))
            throw new GoogleHealthException("invalid_configuration");
        if (!GoogleHealthConnectorConfiguration.IsValidCallbackUrl(options.CallbackUrl))
            throw new GoogleHealthException("invalid_callback");
    }

    public async Task SaveAsync(GoogleHealthOptions options, Guid subject, CancellationToken ct)
    {
        ValidateOptions(options);
        var gate = coordinator.Gate(TenantId);
        await gate.WaitAsync(ct);
        try
        {
            var prior = await StoredOptionsOrNullAsync(ct);
            var session = prior is null ? null : await StoredSessionAsync(ct);
            if (session is not null && prior is not null &&
                (options.ClientId != prior.ClientId || options.CallbackUrl != prior.CallbackUrl))
                throw new GoogleHealthException("disconnect_first");
            if (string.IsNullOrWhiteSpace(options.ClientSecret) && options.ClientId == prior?.ClientId)
                options.ClientSecret = prior.ClientSecret;
            if (string.IsNullOrWhiteSpace(options.ClientSecret))
                throw new GoogleHealthException("client_secret_required");

            coordinator.Flows.TryRemove(TenantId, out _);
            await SaveOptionsAsync(options, subject, ct);
            await connectorConfigurations.UpdateHealthStateAsync(
                ConnectorName,
                lastErrorMessage: string.Empty,
                lastErrorAt: DateTime.MinValue,
                isHealthy: true,
                ct: ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GoogleHealthAuthorize> StartAsync(Guid subject, CancellationToken ct)
    {
        var gate = coordinator.Gate(TenantId);
        await gate.WaitAsync(ct);
        try
        {
            if (await StoredSessionAsync(ct) is not null)
                throw new GoogleHealthException("disconnect_first");
            var settings = await StoredOptionsAsync(ct);
            var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
            var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            coordinator.Flows[TenantId] = new(
                state, verifier, subject, Fingerprint(settings), DateTimeOffset.UtcNow.AddMinutes(10));
            var parameters = new Dictionary<string, string?>
            {
                ["client_id"] = settings.ClientId,
                ["redirect_uri"] = settings.CallbackUrl,
                ["response_type"] = "code",
                ["access_type"] = "offline",
                ["prompt"] = "consent select_account",
                ["scope"] = "openid " + string.Join(' ', settings.DataTypes
                    .Where(type => GoogleHealthClient.SupportedTypes.Contains(type, StringComparer.Ordinal))
                    .Select(GoogleHealthClient.ScopeFor).Distinct()),
                ["state"] = state,
                ["code_challenge_method"] = "S256",
                ["code_challenge"] = WebEncoders.Base64UrlEncode(
                    SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            };
            return new GoogleHealthAuthorize
            {
                Url = QueryHelpers.AddQueryString(
                    "https://accounts.google.com/o/oauth2/v2/auth", parameters)
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CompleteAsync(GoogleHealthCallback callback, Guid subject, CancellationToken ct)
    {
        var gate = coordinator.Gate(TenantId);
        await gate.WaitAsync(ct);
        try
        {
            if (!coordinator.Flows.TryRemove(TenantId, out var flow) ||
                flow.Expires <= DateTimeOffset.UtcNow || flow.SubjectId != subject ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(flow.State), Encoding.UTF8.GetBytes(callback.State)))
                throw new GoogleHealthException("expired_signin");

            var settings = await StoredOptionsAsync(ct);
            if (Fingerprint(settings) != flow.Settings)
                throw new GoogleHealthException("expired_signin");

            var requestedScopes = settings.DataTypes
                .Where(type => GoogleHealthClient.SupportedTypes.Contains(type, StringComparer.Ordinal))
                .Select(GoogleHealthClient.ScopeFor).Append("openid")
                .Distinct(StringComparer.Ordinal).ToArray();
            var token = await oauth.ExchangeAuthorizationCodeAsync(
                Configuration(settings), callback.Code, flow.Verifier, requestedScopes, ct);
            var account = await oauth.AccountKeyAsync(token.AccessToken!, ct);
            var secrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
            if (secrets.GetValueOrDefault(AccountKeySecret) is { } priorAccount && priorAccount != account)
            {
                await oauth.RevokeAsync(token.RefreshToken, ct);
                throw new GoogleHealthException("account_mismatch");
            }

            await SaveSessionAsync(settings, token, account, subject, ct);
            await oauth.StoreSessionAsync(token);
            await connectorConfigurations.UpdateHealthStateAsync(
                ConnectorName,
                lastErrorMessage: string.Empty,
                lastErrorAt: DateTime.MinValue,
                isHealthy: true,
                ct: ct);
        }
        catch (Exception ex) when (ex is GoogleHealthException or HttpRequestException or JsonException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            var error = ex as GoogleHealthException ?? new GoogleHealthException(
                ex is JsonException ? "invalid_google_response" : "google_unavailable",
                stage: ex is JsonException ? "token_response" : "network");
            LogFailure(error);
            await connectorConfigurations.UpdateHealthStateAsync(
                ConnectorName,
                lastErrorMessage: GoogleHealthErrorCode.Encode(
                    error.Message, error.DataType is null ? null : [error.DataType]),
                lastErrorAt: DateTime.UtcNow,
                isHealthy: false,
                ct: CancellationToken.None);
            throw error;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DisconnectAsync(Guid subject, CancellationToken ct)
    {
        var gate = coordinator.Gate(TenantId);
        await gate.WaitAsync(ct);
        try
        {
            var token = await StoredSessionAsync(ct);
            coordinator.Flows.TryRemove(TenantId, out _);
            oauth.InvalidateToken();
            var revokeFailed = false;
            if (token is not null)
            {
                try
                {
                    revokeFailed = !await oauth.RevokeAsync(token.RefreshToken, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    revokeFailed = true;
                }
            }
            await RemoveSessionAsync(subject, removeAccount: false, CancellationToken.None);
            await connectorConfigurations.UpdateHealthStateAsync(
                ConnectorName,
                lastErrorMessage: revokeFailed ? "revoke_in_google" : string.Empty,
                lastErrorAt: revokeFailed ? DateTime.UtcNow : DateTime.MinValue,
                isHealthy: !revokeFailed,
                ct: CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PurgeAsync(Guid subject, CancellationToken ct)
    {
        var gate = coordinator.Gate(TenantId);
        await gate.WaitAsync(ct);
        try
        {
            if (await StoredSessionAsync(ct) is not null)
                throw new GoogleHealthException("disconnect_first");
            if (writer is not null) await writer.PurgeAsync(ct);
            await RemoveSessionAsync(subject, removeAccount: true, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GoogleHealthPreview> PreviewAsync(Guid subject, CancellationToken ct)
    {
        var gate = coordinator.Gate(TenantId);
        await gate.WaitAsync(ct);
        try
        {
            var settings = await StoredOptionsAsync(ct);
            var token = await StoredSessionAsync(ct) ??
                throw new GoogleHealthException("configure_first");
            var now = DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(token.AccessToken) || token.AccessTokenExpiresAt is null ||
                token.AccessTokenExpiresAt <= now.Add(AccessTokenSafety))
            {
                token = await RefreshSessionAsync(settings, token, ct);
                var account = await AccountKeyAsync(ct) ??
                    await oauth.AccountKeyAsync(token.AccessToken!, ct);
                await SaveSessionAsync(settings, token, account, subject, ct);
            }

            var from = settings.ImportFrom ?? now.AddDays(-settings.HistoryDays);
            var items = new List<GoogleHealthPreviewItem>();
            foreach (var capability in GoogleHealthClient.Capabilities)
            {
                var type = capability.DataType;
                if (!capability.Supported)
                {
                    items.Add(new GoogleHealthPreviewItem
                    {
                        DataType = type,
                        Supported = false
                    });
                    continue;
                }
                var granted = token.Scopes.Contains(GoogleHealthClient.ScopeFor(type), StringComparer.Ordinal);
                if (!granted)
                {
                    items.Add(new GoogleHealthPreviewItem
                    {
                        DataType = type,
                        Granted = false,
                        Supported = capability.Supported
                    });
                    continue;
                }
                try
                {
                    var count = await google.CountAsync(token.AccessToken!, type, from, now, ct);
                    items.Add(new GoogleHealthPreviewItem
                    {
                        DataType = type,
                        Granted = true,
                        Count = count,
                        Supported = capability.Supported
                    });
                }
                catch (GoogleHealthException ex)
                {
                    items.Add(new GoogleHealthPreviewItem
                    {
                        DataType = type,
                        Granted = true,
                        ErrorCode = ex.Message,
                        Supported = capability.Supported
                    });
                }
            }
            return new GoogleHealthPreview { Items = items.ToArray() };
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new GoogleHealthException("stored_google_configuration_unreadable", stage: "preview");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task QueueSyncAsync(CancellationToken ct)
    {
        try
        {
            var settings = await StoredOptionsAsync(ct);
            if (await StoredSessionAsync(ct) is null)
                throw new GoogleHealthException("configure_first");
            if (settings.PreviewOnly) throw new GoogleHealthException("preview_required");
            if (settings.DataTypes.Length == 0) throw new GoogleHealthException("no_types_selected");
            coordinator.Queue(TenantId, settings.DataTypes.Length);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new GoogleHealthException("stored_google_configuration_unreadable", stage: "sync_queue");
        }
    }

    private GoogleHealthStatus WithProgress(GoogleHealthStatus status)
    {
        var progress = coordinator.Progress(TenantId);
        if (progress is null) return status;
        status.IsSyncing = true;
        status.SyncPhase = progress.Phase;
        status.SyncDataType = progress.DataType;
        status.SyncCompletedDataTypes = progress.CompletedDataTypes;
        status.SyncTotalDataTypes = progress.TotalDataTypes;
        status.SyncPagesRead = progress.PagesRead;
        status.SyncProgressPercent = progress.Phase switch
        {
            GoogleHealthSyncPhase.Integrating => 95,
            _ when progress.TotalDataTypes > 0 =>
                Math.Min(90, progress.CompletedDataTypes * 90 / progress.TotalDataTypes),
            _ => null
        };
        return status;
    }

    private async Task<string?> AccountKeyAsync(CancellationToken ct) =>
        (await connectorConfigurations.GetSecretsAsync(ConnectorName, ct))
        .GetValueOrDefault(AccountKeySecret);

    private void LogFailure(GoogleHealthException error) => logger?.LogWarning(
        "Google Health request failed for tenant {TenantId} with code {Code} at stage {Stage} for data type {DataType}; provider status {ProviderStatus}, provider reason {ProviderReason}",
        TenantId, error.Message, error.Stage, error.DataType,
        error.ProviderStatus, error.ProviderReason);

    private static DateTimeOffset? AsOffset(DateTime? value) => value is null
        ? null
        : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static string[] SelectedTypes(GoogleHealthConnectorConfiguration configuration)
    {
        var types = new List<string>();
        if (configuration.SyncSteps) types.Add("steps");
        if (configuration.SyncHeartRate) types.Add("heart-rate");
        if (configuration.SyncBodyWeight) types.Add("weight");
        if (configuration.SyncSleep) types.Add("sleep");
        return types.ToArray();
    }

    private static GoogleHealthConnectorConfiguration Configuration(
        GoogleHealthOptions settings,
        string? refreshToken = null) => new()
    {
        ClientId = settings.ClientId,
        ClientSecret = settings.ClientSecret,
        CallbackUrl = settings.CallbackUrl,
        RefreshToken = refreshToken
    };

    private static string Fingerprint(GoogleHealthOptions options) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(options, Json)));
}
