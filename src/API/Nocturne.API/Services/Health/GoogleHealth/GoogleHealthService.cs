using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Models;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Health;

namespace Nocturne.API.Services.Health.GoogleHealth;

public sealed class GoogleHealthService(
    GoogleHealthCoordinator coordinator,
    GoogleHealthClient google,
    GoogleHealthAuthTokenProvider oauth,
    GoogleHealthSessionStore sessions,
    IConnectorConfigurationService connectorConfigurations,
    IConnectorConfigurationLoader<GoogleHealthConnectorConfiguration> configurationLoader,
    ITenantAccessor tenantAccessor,
    IConnectorSyncCursorStore cursorStore,
    IPatientRecordRepository patientRecords,
    IOptions<GoogleHealthPlatformOptions> platformOptions,
    GoogleHealthOAuthState oauthState,
    IGoogleHealthReadingWriter writer,
    ILogger<GoogleHealthService> logger) : IGoogleHealthService
{
    private const string ConnectorName = "GoogleHealth";
    private const string InventoryKey = "inventory";
    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PreviewWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan PreviewGateTimeout = TimeSpan.FromSeconds(5);
    // Dense heart-rate inventories can require minutes of paging even within a seven-day window.
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Guid TenantId => tenantAccessor.TenantId;
    private GoogleHealthPlatformOptions Platform => platformOptions.Value;

    private async Task<GoogleHealthConnectorConfiguration?> StoredConfigurationOrNullAsync(CancellationToken ct) =>
        await connectorConfigurations.GetConfigurationAsync(ConnectorName, ct) is null
            ? null
            : await configurationLoader.LoadForTenantAsync(ct);

    private async Task<GoogleHealthConnectorConfiguration> StoredConfigurationAsync(CancellationToken ct) =>
        await StoredConfigurationOrNullAsync(ct) ?? throw new GoogleHealthException("configure_first");

    private bool HasLiveSession(GoogleHealthConnectorConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.RefreshToken) && !configuration.IssuedToAnotherClient(Platform);

    private static GoogleHealthOptions Options(GoogleHealthConnectorConfiguration configuration) => new()
    {
        DataTypes = SelectedTypes(configuration),
        HistoryDays = configuration.HistoryDays,
        ImportFrom = string.IsNullOrWhiteSpace(configuration.ImportFrom)
            ? null
            : DateTimeOffset.Parse(configuration.ImportFrom, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        PreviewOnly = configuration.PreviewOnly
    };

    private async Task SaveOptionsAsync(GoogleHealthOptions options, Guid subject, CancellationToken ct)
    {
        var stored = await connectorConfigurations.GetConfigurationAsync(ConnectorName, ct);
        var configuration = stored?.Configuration.RootElement.Deserialize<JsonObject>(Json) ?? new JsonObject();
        configuration.Remove("callbackUrl");
        configuration.Remove("enabled");
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
    }

    public async Task<GoogleHealthStatus> StatusAsync(CancellationToken ct)
    {
        try
        {
            var stored = await connectorConfigurations.GetConfigurationAsync(ConnectorName, ct);
            if (stored is null)
                return await WithProgressAsync(new GoogleHealthStatus());

            var configuration = await configurationLoader.LoadForTenantAsync(ct);
            var settings = Options(configuration);
            var clientChanged = configuration.IssuedToAnotherClient(Platform);
            var session = clientChanged ? null : await sessions.CurrentAsync(configuration);
            var selected = settings.DataTypes
                .Where(type => GoogleHealthClient.SupportedTypes.Contains(type, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var storedError = GoogleHealthErrorCode.Decode(stored.LastErrorMessage);
            var runtime = await RuntimeStateAsync(ct);
            var backfillProgress = BackfillProgress(runtime);
            var missingScopes = selected
                .Where(type => session is not null &&
                    !session.Scopes.Contains(GoogleHealthClient.ScopeFor(type), StringComparer.Ordinal))
                .ToArray();
            var errorCode = clientChanged
                ? "client_changed"
                : selected.Length != settings.DataTypes.Length
                    ? "unsupported_type"
                    : missingScopes.Length > 0 ? "partial_consent" : storedError.Code;
            var errorDataTypes = missingScopes.Length > 0 ? missingScopes : storedError.DataTypes;

            return await WithProgressAsync(new GoogleHealthStatus
            {
                Configured = true,
                Connected = session is not null,
                SelectedTypes = selected,
                Categories = GoogleHealthInventory.Categories(Inventory(runtime), new(
                    session?.Scopes ?? [],
                    selected,
                    settings.PreviewOnly,
                    errorCode,
                    errorDataTypes,
                    await IncludeCycleTrackingAsync(ct))),
                HistoryDays = settings.HistoryDays,
                ImportFrom = settings.ImportFrom,
                LastAttempt = AsOffset(stored.LastSyncAttempt),
                LastSync = AsOffset(stored.LastSuccessfulSync),
                BackfillSyncedThrough = backfillProgress.SyncedThrough,
                BackfillComplete = backfillProgress.Complete,
                ErrorCode = errorCode,
                ErrorDataTypes = errorDataTypes,
                PreviewRequired = settings.PreviewOnly
            });
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            logger.LogWarning(ex,
                "Google Health configuration could not be read for tenant {TenantId}", TenantId);
            return await WithProgressAsync(new GoogleHealthStatus
            {
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
        if (options.HistoryDays is < 1 or > 90 ||
            options.ImportFrom is { } importFrom &&
            (importFrom < new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero) ||
             importFrom > DateTimeOffset.UtcNow.AddDays(1)))
            throw new GoogleHealthException("invalid_configuration");
    }

    public async Task SaveAsync(GoogleHealthOptions options, Guid subject, CancellationToken ct)
    {
        ValidateOptions(options);
        await using var gate = await coordinator.AcquireAsync(TenantId, ct);
        var configuration = await StoredConfigurationOrNullAsync(ct);
        var prior = configuration is null ? null : Options(configuration);
        await ClearFlowAsync(ct);
        if (prior is not null && (options.ImportFrom != prior.ImportFrom || options.HistoryDays != prior.HistoryDays ||
                                 !options.DataTypes.Order(StringComparer.Ordinal).SequenceEqual(prior.DataTypes.Order(StringComparer.Ordinal))))
        {
            var state = await RuntimeStateAsync(ct);
            foreach (var key in new[] { "backfillCursorDate", "backfillFloorDate", "backfillComplete", "backfillChunkDays" })
                state.Remove(key);
            if (!options.DataTypes.Order(StringComparer.Ordinal).SequenceEqual(prior.DataTypes.Order(StringComparer.Ordinal)))
                state.Remove("lastSyncedTo");
            await SaveRuntimeStateAsync(state, ct);
        }
        await SaveOptionsAsync(options, subject, ct);
    }

    public async Task<GoogleHealthAuthorize> StartAsync(Guid subject, string? tenantSlug, CancellationToken ct)
    {
        if (!Platform.IsConfigured)
            throw new GoogleHealthException("platform_not_configured");
        await using var gate = await coordinator.AcquireAsync(TenantId, ct);
        var configuration = await StoredConfigurationAsync(ct);
        if (configuration.IssuedToAnotherClient(Platform))
            await sessions.ClearAsync(removeAccount: false, subject, revoke: true, ct);
        else if (!string.IsNullOrWhiteSpace(configuration.RefreshToken))
            throw new GoogleHealthException("disconnect_first");
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var expires = DateTimeOffset.UtcNow.Add(FlowLifetime);
        var state = oauthState.Protect(tenantSlug, expires);
        var flow = new GoogleHealthCoordinator.Flow(state, verifier, subject, expires);
        var secrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
        secrets["oauthFlow"] = JsonSerializer.Serialize(flow, Json);
        await connectorConfigurations.SaveSecretsAsync(ConnectorName, secrets, subject.ToString(), ct);
        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = Platform.ClientId,
            ["redirect_uri"] = Platform.RedirectUri,
            ["response_type"] = "code",
            ["access_type"] = "offline",
            ["include_granted_scopes"] = "true",
            ["prompt"] = "consent select_account",
            ["scope"] = "openid " + string.Join(' ', GoogleHealthClient.SupportedTypes
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

    public async Task CompleteAsync(GoogleHealthCallback callback, Guid subject, CancellationToken ct)
    {
        await using var gate = await coordinator.AcquireAsync(TenantId, ct);
        try
        {
            var flowSecrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
            var flowJson = flowSecrets.GetValueOrDefault("oauthFlow");
            var flow = flowJson is null ? null : JsonSerializer.Deserialize<GoogleHealthCoordinator.Flow>(flowJson, Json);
            if (flow is null ||
                flow.Expires <= DateTimeOffset.UtcNow || flow.SubjectId != subject ||
                string.IsNullOrEmpty(callback.State) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(flow.State), Encoding.UTF8.GetBytes(callback.State)))
                throw new GoogleHealthException("expired_signin");

            await ClearFlowAsync(ct);
            var requestedScopes = GoogleHealthClient.SupportedTypes
                .Select(GoogleHealthClient.ScopeFor).Append("openid")
                .Distinct(StringComparer.Ordinal).ToArray();
            var token = await oauth.ExchangeAuthorizationCodeAsync(
                callback.Code, flow.Verifier, requestedScopes, ct);
            var stored = false;
            string? account = null;
            try
            {
                account = await oauth.AccountKeyAsync(token.AccessToken!, ct);
                if (await sessions.AccountKeyAsync(ct) is { } priorAccount && priorAccount != account)
                    throw new GoogleHealthException("account_mismatch");

                await sessions.StoreAsync(token, account, subject, ct);
                stored = true;
            }
            finally
            {
                // StartAsync refuses while a refresh token is stored, so this grant is not one the
                // tenant still relies on, and unrevoked it would stay live at Google.
                if (!stored) await DiscardUnstoredGrantAsync(token.RefreshToken, account);
            }
            await ActivateAsync(token, ct);
        }
        catch (Exception ex) when (ex is GoogleHealthException or HttpRequestException or JsonException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            var error = ex as GoogleHealthException ?? new GoogleHealthException(
                ex is JsonException ? "invalid_google_response" : "google_unavailable",
                stage: ex is JsonException ? "token_response" : "network");
            LogFailure(ex, error);
            await connectorConfigurations.UpdateHealthStateAsync(
                ConnectorName,
                lastErrorMessage: GoogleHealthErrorCode.Encode(
                    error.Message, error.DataType is null ? null : [error.DataType]),
                lastErrorAt: DateTime.UtcNow,
                isHealthy: false,
                ct: CancellationToken.None);
            throw error;
        }
    }

    /// <summary>
    /// The work after the grant is stored. The tenant already syncs on the grant alone, so a
    /// failure here is a warning and never a reason to revoke it.
    /// </summary>
    private async Task ActivateAsync(GoogleHealthTokenSession token, CancellationToken ct)
    {
        try
        {
            await sessions.CacheAsync(token);
            await ForgetInventoryAsync(ct);
            await connectorConfigurations.UpdateHealthStateAsync(
                ConnectorName,
                lastErrorMessage: string.Empty,
                lastErrorAt: DateTime.MinValue,
                isHealthy: true,
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Google Health could not finish activating the stored grant of tenant {TenantId}", TenantId);
            await connectorConfigurations.UpdateHealthStateAsync(
                ConnectorName,
                lastErrorMessage: GoogleHealthErrorCode.Encode("activation_failed"),
                lastErrorAt: DateTime.UtcNow,
                isHealthy: false,
                ct: CancellationToken.None);
        }
    }

    private async Task DiscardUnstoredGrantAsync(string refreshToken, string? account)
    {
        try
        {
            await sessions.ReleaseUnstoredAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Google Health could not release the hold of tenant {TenantId} on an unstored grant", TenantId);
        }
        if (await sessions.RevokeAsync(refreshToken, account, CancellationToken.None) == GoogleHealthRevocation.Failed)
            logger.LogWarning("Google Health could not revoke an unstored grant for tenant {TenantId}", TenantId);
    }

    public async Task DisconnectAsync(Guid subject, CancellationToken ct)
    {
        var request = await coordinator.RequestDisconnectAsync(TenantId, subject, ct);
        await ResumeDisconnectAsync(subject, request);
    }

    public async Task ResumeDisconnectAsync(Guid subject, Guid request)
    {
        // Once accepted, refreshing the browser must not cancel local disconnection.
        await using var gate = await coordinator.AcquireAsync(TenantId, CancellationToken.None);
        if ((await coordinator.DisconnectRequestAsync(TenantId, CancellationToken.None))?.RequestId != request) return;
        await coordinator.CompleteAsync(TenantId);
        var refreshToken = (await configurationLoader.LoadForTenantAsync(CancellationToken.None)).RefreshToken;
        var account = await sessions.AccountKeyAsync(CancellationToken.None);
        await ClearFlowAsync(CancellationToken.None);
        await sessions.ClearAsync(removeAccount: false, subject, revoke: false, CancellationToken.None);
        await ForgetInventoryAsync(CancellationToken.None);
        // Only a grant no other tenant holds is worth revoking in Google by hand. An undetermined
        // grant may be another tenant's, which that advice would cut off.
        var revokeFailed = !string.IsNullOrWhiteSpace(refreshToken) &&
            await sessions.RevokeAsync(refreshToken, account, CancellationToken.None) == GoogleHealthRevocation.Failed;
        await connectorConfigurations.UpdateHealthStateAsync(
            ConnectorName,
            lastErrorMessage: revokeFailed ? "revoke_in_google" : string.Empty,
            lastErrorAt: revokeFailed ? DateTime.UtcNow : DateTime.MinValue,
            isHealthy: !revokeFailed,
            ct: CancellationToken.None);
        await coordinator.FinishDisconnectAsync(TenantId, request);
    }

    public async Task PurgeAsync(Guid subject, CancellationToken ct)
    {
        await using var gate = await coordinator.AcquireAsync(TenantId, ct);
        if (HasLiveSession(await configurationLoader.LoadForTenantAsync(ct)))
            throw new GoogleHealthException("disconnect_first");
        await ClearFlowAsync(ct);
        await coordinator.CompleteAsync(TenantId);
        await writer.PurgeAsync(ct);
        await cursorStore.SetAsync(ConnectorName, GoogleHealthConnectorService.RuntimeStateResource, new(null, null), ct);
        var stored = await connectorConfigurations.GetConfigurationAsync(ConnectorName, ct);
        if (stored?.Configuration is not null)
        {
            var configuration = JsonNode.Parse(stored.Configuration.RootElement.GetRawText())!.AsObject();
            foreach (var key in new[] { "lastSyncedTo", "backfillCursorDate", "backfillFloorDate",
                         "backfillComplete", "backfillChunkDays" })
                configuration.Remove(key);
            using var document = JsonDocument.Parse(configuration.ToJsonString(Json));
            await connectorConfigurations.SaveConfigurationAsync(ConnectorName, document, subject.ToString(), ct);
        }
        await sessions.ClearAsync(removeAccount: true, subject, revoke: false, ct);
    }

    public async Task<GoogleHealthStatus> PreviewAsync(Guid subject, CancellationToken ct)
    {
        if (!Platform.IsConfigured)
            throw new GoogleHealthException("platform_not_configured", stage: "preview");
        await using var reader = await coordinator.WatchReadsAsync(TenantId, ct);
        ct = reader.Token;
        await using var gate = await coordinator.AcquireAsync(TenantId, ct, PreviewGateTimeout);
        if (gate is null)
            throw new GoogleHealthException("already_running", stage: "preview");
        try
        {
            using var previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            previewCancellation.CancelAfter(PreviewTimeout);
            var previewCt = previewCancellation.Token;
            GoogleHealthConnectorConfiguration configuration;
            try
            {
                configuration = await StoredConfigurationAsync(ct);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                throw new GoogleHealthException("stored_google_configuration_unreadable", stage: "preview");
            }

            if (string.IsNullOrWhiteSpace(configuration.RefreshToken))
                throw new GoogleHealthException("configure_first");
            var session = await sessions.RefreshAsync(configuration, force: false, previewCt);
            var to = DateTimeOffset.UtcNow;
            var inventory = await sessions.WithAccessTokenAsync(configuration, session.AccessToken!,
                token => ReadInventoryAsync(token, configuration.ParsedGrantedScopes(), to - PreviewWindow, to, previewCt),
                previewCt);
            var state = await RuntimeStateAsync(ct);
            state[InventoryKey] = JsonSerializer.SerializeToElement(inventory, Json);
            await SaveRuntimeStateAsync(state, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GoogleHealthException("google_unavailable", stage: "preview_timeout");
        }
        catch (GoogleHealthException ex) when (ex.RequiresReconnect)
        {
            await sessions.ClearAsync(removeAccount: false, subject, revoke: ex.Message == "client_changed", ct);
            await connectorConfigurations.UpdateHealthStateAsync(ConnectorName,
                lastErrorMessage: GoogleHealthErrorCode.Encode(ex.Message, ex.DataType is null ? null : [ex.DataType]),
                lastErrorAt: DateTime.UtcNow, isHealthy: false, ct: ct);
            throw;
        }
        return await StatusAsync(ct);
    }

    private async Task<GoogleHealthInventoryItem[]> ReadInventoryAsync(
        string accessToken, string[] grantedScopes, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        await Task.WhenAll(GoogleHealthClient.SupportedTypes.Select(async type =>
        {
            if (!grantedScopes.Contains(GoogleHealthClient.ScopeFor(type), StringComparer.Ordinal))
                return new GoogleHealthInventoryItem(type, 0, null);
            try
            {
                return new GoogleHealthInventoryItem(type, await google.CountAsync(accessToken, type, from, to, ct), null);
            }
            catch (GoogleHealthException ex) when (ex.Message != "access_token_rejected")
            {
                return new GoogleHealthInventoryItem(type, 0, ex.Message);
            }
        }));

    public async Task QueueSyncAsync(CancellationToken ct)
    {
        if (!Platform.IsConfigured)
            throw new GoogleHealthException("platform_not_configured", stage: "sync_queue");
        try
        {
            if (await coordinator.DisconnectRequestAsync(TenantId, ct) is not null)
                throw new GoogleHealthException("disconnect_first");
            var configuration = await StoredConfigurationAsync(ct);
            if (!HasLiveSession(configuration))
                throw new GoogleHealthException("configure_first");
            var settings = Options(configuration);
            if (settings.PreviewOnly) throw new GoogleHealthException("preview_required");
            if (settings.DataTypes.Length == 0) throw new GoogleHealthException("no_types_selected");
            if (!await coordinator.QueueAsync(TenantId, settings.DataTypes.Length, ct))
                throw new GoogleHealthException("already_running", stage: "sync_queue");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new GoogleHealthException("stored_google_configuration_unreadable", stage: "sync_queue");
        }
    }

    private async Task<GoogleHealthStatus> WithProgressAsync(GoogleHealthStatus status)
    {
        status.Available = Platform.IsConfigured;
        status.IsDisconnecting = await coordinator.DisconnectRequestAsync(TenantId, CancellationToken.None) is not null;
        var progress = await coordinator.ProgressAsync(TenantId, CancellationToken.None);
        status.ConnectionState = ConnectionState(status, progress is not null);
        if (progress is null) return status;
        status.IsSyncing = true;
        status.SyncPhase = progress.Phase;
        status.SyncDataType = progress.DataType;
        status.SyncCompletedDataTypes = progress.CompletedDataTypes;
        status.SyncTotalDataTypes = progress.TotalDataTypes;
        status.SyncPagesRead = progress.PagesRead;
        status.SyncProgressPercent = progress.Phase switch
        {
            _ when progress.TotalDataTypes > 0 =>
                Math.Min(99, (progress.CompletedDataTypes * 100 +
                    (progress.Phase == GoogleHealthSyncPhase.Integrating ? 90 : 0)) / progress.TotalDataTypes),
            _ => null
        };
        return status;
    }

    internal static GoogleHealthConnectionState ConnectionState(GoogleHealthStatus status, bool syncing) => status switch
    {
        { Available: false } => GoogleHealthConnectionState.Unavailable,
        { Connected: false } => GoogleHealthConnectionState.Disconnected,
        { PreviewRequired: true } => GoogleHealthConnectionState.ReviewRequired,
        { SelectedTypes.Length: 0 } => GoogleHealthConnectionState.NothingSelected,
        _ when syncing => GoogleHealthConnectionState.Syncing,
        _ => GoogleHealthConnectionState.Active
    };

    private async Task<bool> IncludeCycleTrackingAsync(CancellationToken ct) =>
        (await patientRecords.GetAsync(ct))?.Sex != BiologicalSex.Male;

    private async Task<Dictionary<string, JsonElement>> RuntimeStateAsync(CancellationToken ct) =>
        GoogleHealthConnectorService.ParseRuntimeState(
            (await cursorStore.GetAsync(ConnectorName, GoogleHealthConnectorService.RuntimeStateResource, ct))?.LastGuid);

    private Task SaveRuntimeStateAsync(Dictionary<string, JsonElement> state, CancellationToken ct) =>
        cursorStore.SetAsync(ConnectorName, GoogleHealthConnectorService.RuntimeStateResource,
            new(null, JsonSerializer.Serialize(state, Json)), ct);

    /// <summary>A scan belongs to the session that ran it; a new or ended session has none.</summary>
    private async Task ForgetInventoryAsync(CancellationToken ct)
    {
        var state = await RuntimeStateAsync(ct);
        if (state.Remove(InventoryKey)) await SaveRuntimeStateAsync(state, ct);
    }

    private static GoogleHealthInventoryItem[]? Inventory(IReadOnlyDictionary<string, JsonElement> state)
    {
        if (!state.TryGetValue(InventoryKey, out var inventory)) return null;
        try { return inventory.Deserialize<GoogleHealthInventoryItem[]>(Json); }
        catch (JsonException) { return null; }
    }

    private async Task ClearFlowAsync(CancellationToken ct)
    {
        var secrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
        if (!secrets.Remove("oauthFlow")) return;
        await connectorConfigurations.SaveSecretsAsync(ConnectorName, secrets, ct: ct);
    }

    private void LogFailure(Exception ex, GoogleHealthException error) => logger.LogError(ex,
        "Google Health request failed for tenant {TenantId} with code {Code} at stage {Stage} for data type {DataType}; provider status {ProviderStatus}, provider reason {ProviderReason}",
        TenantId, error.Message, error.Stage, error.DataType,
        error.ProviderStatus, error.ProviderReason);

    private static DateTimeOffset? AsOffset(DateTime? value) => value is null
        ? null
        : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static (DateTimeOffset? SyncedThrough, bool Complete) BackfillProgress(IReadOnlyDictionary<string, JsonElement> configuration)
    {
        DateTimeOffset? syncedThrough = null;
        if (configuration.TryGetValue("backfillCursorDate", out var cursor) &&
            cursor.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(cursor.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            syncedThrough = parsed;
        var complete = configuration.TryGetValue("backfillComplete", out var completed) &&
            completed.ValueKind == JsonValueKind.True;
        return (syncedThrough, complete);
    }

    private static string[] SelectedTypes(GoogleHealthConnectorConfiguration configuration)
    {
        var types = new List<string>();
        if (configuration.SyncSteps) types.Add("steps");
        if (configuration.SyncHeartRate) types.Add("heart-rate");
        if (configuration.SyncBodyWeight) types.Add("weight");
        if (configuration.SyncSleep) types.Add("sleep");
        return types.ToArray();
    }
}
