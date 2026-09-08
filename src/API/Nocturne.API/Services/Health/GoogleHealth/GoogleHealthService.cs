using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Models;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Models.Health;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Health.GoogleHealth;

public sealed class GoogleHealthCoordinator
{
    internal sealed record Flow(string State, string Verifier, Guid SubjectId, string Settings, DateTimeOffset Expires);
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
    public SemaphoreSlim Gate(Guid tenant) => Locks.GetOrAdd(tenant, _ => new SemaphoreSlim(1));

    internal bool Queue(Guid tenant, int totalDataTypes)
    {
        if (!syncProgress.TryAdd(tenant, new(GoogleHealthSyncPhase.Queued, null, 0, totalDataTypes, 0))) return false;
        if (syncRequests.Writer.TryWrite(tenant)) return true;
        syncProgress.TryRemove(tenant, out _);
        return false;
    }

    internal IAsyncEnumerable<Guid> ReadRequestsAsync(CancellationToken ct) =>
        syncRequests.Reader.ReadAllAsync(ct);

    internal bool StartQueued(Guid tenant) => Update(tenant, current =>
        current.Phase == GoogleHealthSyncPhase.Queued
            ? current with { Phase = GoogleHealthSyncPhase.Preparing }
            : null);

    internal bool StartScheduled(Guid tenant) =>
        syncProgress.TryAdd(tenant, new(GoogleHealthSyncPhase.Preparing, null, 0, 0, 0));

    internal void Report(Guid tenant, GoogleHealthSyncPhase phase, string? dataType = null,
        int? completedDataTypes = null, int? totalDataTypes = null, int? pagesRead = null) =>
        Update(tenant, current => current with
        {
            Phase = phase,
            DataType = dataType,
            CompletedDataTypes = completedDataTypes ?? current.CompletedDataTypes,
            TotalDataTypes = totalDataTypes ?? current.TotalDataTypes,
            PagesRead = pagesRead ?? current.PagesRead
        });

    internal SyncProgress? Progress(Guid tenant) =>
        syncProgress.TryGetValue(tenant, out var progress) ? progress : null;

    internal void Complete(Guid tenant) => syncProgress.TryRemove(tenant, out _);

    private bool Update(Guid tenant, Func<SyncProgress, SyncProgress?> update)
    {
        while (syncProgress.TryGetValue(tenant, out var current))
        {
            var next = update(current);
            if (next is null) return false;
            if (syncProgress.TryUpdate(tenant, next, current)) return true;
        }
        return false;
    }
}

public sealed class GoogleHealthService(NocturneDbContext db, IDataProtectionProvider protection,
    GoogleHealthCoordinator coordinator, GoogleHealthClient google, GoogleHealthAuthTokenProvider oauth,
    IGoogleHealthReadingWriter? writer = null,
    ILogger<GoogleHealthService>? logger = null,
    IConnectorConfigurationService? connectorConfigurations = null,
    IConnectorConfigurationLoader<GoogleHealthConnectorConfiguration>? configurationLoader = null)
    : IGoogleHealthService
{
    private const string ConnectorName = "GoogleHealth";
    private static readonly TimeSpan AccessTokenSafety = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IDataProtector Protector => protection.CreateProtector("Nocturne.GoogleHealth.v1", db.TenantId.ToString());
    private string Protect<T>(T value) => Protector.Protect(JsonSerializer.Serialize(value, Json));
    private T Unprotect<T>(string value) => JsonSerializer.Deserialize<T>(Protector.Unprotect(value), Json) ?? throw new JsonException();
    private Task<GoogleHealthConnectionEntity?> Connection(CancellationToken ct) => db.GoogleHealthConnections.SingleOrDefaultAsync(ct);

    private async Task<GoogleHealthOptions?> SharedOptionsAsync(CancellationToken ct)
    {
        if (configurationLoader is null)
            return null;

        var configuration = await configurationLoader.LoadForTenantAsync(ct);
        if (!configuration.Enabled)
            return null;

        DateTimeOffset? importFrom = null;
        if (!string.IsNullOrWhiteSpace(configuration.ImportFrom))
            importFrom = DateTimeOffset.Parse(
                configuration.ImportFrom,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

        var dataTypes = new List<string>();
        if (configuration.SyncSteps) dataTypes.Add("steps");
        if (configuration.SyncHeartRate) dataTypes.Add("heart-rate");
        if (configuration.SyncBodyWeight) dataTypes.Add("weight");
        if (configuration.SyncSleep) dataTypes.Add("sleep");

        return new GoogleHealthOptions
        {
            ClientId = configuration.ClientId,
            ClientSecret = configuration.ClientSecret,
            CallbackUrl = configuration.CallbackUrl,
            DataTypes = dataTypes.ToArray(),
            HistoryDays = configuration.HistoryDays,
            ImportFrom = importFrom,
            PreviewOnly = configuration.PreviewOnly
        };
    }

    private static string ConfigurationFingerprint(GoogleHealthOptions options) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(options, Json)));

    private async Task<GoogleHealthOptions> StoredOptionsAsync(
        GoogleHealthConnectionEntity connection,
        CancellationToken ct)
    {
        var settings = await SharedOptionsAsync(ct) ??
            Unprotect<GoogleHealthOptions>(connection.ProtectedSettings);
        if (settings.DataTypes is null)
            throw new JsonException();
        return settings;
    }

    private async Task<GoogleHealthTokenSession?> SharedSessionAsync(CancellationToken ct)
    {
        if (configurationLoader is null)
            return null;

        var configuration = await configurationLoader.LoadForTenantAsync(ct);
        if (!configuration.Enabled || string.IsNullOrWhiteSpace(configuration.RefreshToken))
            return null;

        GoogleHealthTokenSession? cached;
        try
        {
            cached = await oauth.GetCurrentSessionAsync();
        }
        catch (GoogleHealthException)
        {
            oauth.InvalidateToken();
            cached = null;
        }
        if (cached is not null &&
            string.Equals(cached.RefreshToken, configuration.RefreshToken, StringComparison.Ordinal))
            return cached;

        var scopes = (configuration.GrantedScopes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new GoogleHealthTokenSession(configuration.RefreshToken, scopes);
    }

    private async Task<GoogleHealthTokenSession?> StoredSessionAsync(
        GoogleHealthConnectionEntity connection,
        CancellationToken ct)
    {
        var session = await SharedSessionAsync(ct) ??
            (connection.ProtectedToken is null
                ? null
                : Unprotect<GoogleHealthTokenSession>(connection.ProtectedToken));
        if (session is not null &&
            (string.IsNullOrWhiteSpace(session.RefreshToken) || session.Scopes is null))
            throw new JsonException();
        return session;
    }

    private async Task MirrorOptionsAsync(
        GoogleHealthOptions options,
        Guid subject,
        CancellationToken ct)
    {
        if (connectorConfigurations is null)
            return;

        using var configuration = JsonSerializer.SerializeToDocument(new
        {
            enabled = true,
            clientId = options.ClientId,
            callbackUrl = options.CallbackUrl,
            lookbackDays = options.HistoryDays,
            importFrom = options.ImportFrom?.ToString("O", CultureInfo.InvariantCulture),
            previewOnly = options.PreviewOnly,
            syncSteps = options.DataTypes.Contains("steps", StringComparer.Ordinal),
            syncHeartRate = options.DataTypes.Contains("heart-rate", StringComparer.Ordinal),
            syncBodyWeight = options.DataTypes.Contains("weight", StringComparer.Ordinal),
            syncSleep = options.DataTypes.Contains("sleep", StringComparer.Ordinal)
        }, Json);
        await connectorConfigurations.SaveConfigurationAsync(
            ConnectorName,
            configuration,
            subject.ToString(),
            ct);

        var secrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
        secrets["clientSecret"] = options.ClientSecret!;
        await connectorConfigurations.SaveSecretsAsync(
            ConnectorName,
            secrets,
            subject.ToString(),
            ct);
    }

    private async Task MirrorTokenAsync(
        GoogleHealthOptions settings,
        GoogleHealthTokenSession token,
        Guid subject,
        CancellationToken ct)
    {
        if (connectorConfigurations is null)
            return;

        var secrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
        secrets["clientSecret"] = settings.ClientSecret!;
        secrets["refreshToken"] = token.RefreshToken;
        secrets["grantedScopes"] = string.Join(' ', token.Scopes.Distinct(StringComparer.Ordinal));
        await connectorConfigurations.SaveSecretsAsync(
            ConnectorName,
            secrets,
            subject.ToString(),
            ct);
    }

    private async Task ClearMirroredTokenAsync(Guid subject, CancellationToken ct)
    {
        if (connectorConfigurations is null)
            return;

        var secrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
        secrets.Remove("refreshToken");
        secrets.Remove("grantedScopes");
        await connectorConfigurations.SaveSecretsAsync(
            ConnectorName,
            secrets,
            subject.ToString(),
            ct);
    }

    private static string EncodeError(string code, IEnumerable<string>? dataTypes = null)
    {
        var types = dataTypes?.Where(type => GoogleHealthClient.SupportedTypes.Contains(type, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray() ?? [];
        return types.Length == 0 ? code : $"{code}:{string.Join(',', types)}";
    }

    private static (string? Code, string[] DataTypes) DecodeError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, []);
        var separator = value.IndexOf(':');
        if (separator < 0) return (value, []);
        return (value[..separator], value[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(type => GoogleHealthClient.SupportedTypes.Contains(type, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray());
    }

    private void LogFailure(GoogleHealthException error) => logger?.LogWarning(
        "Google Health import failed for tenant {TenantId} with code {Code} at stage {Stage} for data type {DataType}; provider status {ProviderStatus}, provider reason {ProviderReason}",
        db.TenantId, error.Message, error.Stage, error.DataType, error.ProviderStatus, error.ProviderReason);

    private GoogleHealthStatus WithProgress(GoogleHealthStatus status)
    {
        var progress = coordinator.Progress(db.TenantId);
        if (progress is null) return status;
        status.IsSyncing = true;
        status.SyncPhase = progress.Phase;
        status.SyncDataType = progress.DataType;
        status.SyncCompletedDataTypes = progress.CompletedDataTypes;
        status.SyncTotalDataTypes = progress.TotalDataTypes;
        status.SyncPagesRead = progress.PagesRead;
        status.SyncProgressPercent = progress.Phase switch
        {
            GoogleHealthSyncPhase.Saving or GoogleHealthSyncPhase.Integrating => 95,
            _ when progress.TotalDataTypes > 0 =>
                Math.Min(90, progress.CompletedDataTypes * 90 / progress.TotalDataTypes),
            _ => null
        };
        return status;
    }

    private static GoogleHealthConnectorConfiguration ConnectorConfiguration(
        GoogleHealthOptions settings,
        string? refreshToken = null) => new()
    {
        ClientId = settings.ClientId,
        ClientSecret = settings.ClientSecret,
        CallbackUrl = settings.CallbackUrl,
        RefreshToken = refreshToken
    };

    private async Task<GoogleHealthTokenSession> RefreshSessionAsync(
        GoogleHealthOptions settings,
        GoogleHealthTokenSession token,
        CancellationToken ct,
        bool forceRefresh = false)
    {
        await oauth.SeedSessionAsync(token);
        if (forceRefresh)
            oauth.InvalidateToken();

        var accessToken = await oauth.GetValidTokenAsync(
            ConnectorConfiguration(settings, token.RefreshToken),
            ct);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new GoogleHealthException("invalid_token_response", stage: "token_refresh");

        return await oauth.GetCurrentSessionAsync() ??
               throw new GoogleHealthException("invalid_token_response", stage: "token_cache");
    }

    public async Task<GoogleHealthStatus> StatusAsync(CancellationToken ct)
    {
        var row = await Connection(ct);
        var sharedSettings = await SharedOptionsAsync(ct);
        if (row is null && sharedSettings is null)
            return WithProgress(new() { Capabilities = GoogleHealthClient.Capabilities });

        if (row is null)
            return WithProgress(new()
            {
                Capabilities = GoogleHealthClient.Capabilities,
                Configured = true,
                ClientId = sharedSettings!.ClientId,
                CallbackUrl = sharedSettings.CallbackUrl,
                HistoryDays = sharedSettings.HistoryDays,
                ImportFrom = sharedSettings.ImportFrom,
                SelectedTypes = sharedSettings.DataTypes,
                PreviewRequired = sharedSettings.PreviewOnly
            });

        GoogleHealthOptions settings;
        try
        {
            settings = sharedSettings ?? Unprotect<GoogleHealthOptions>(row.ProtectedSettings);
            if (settings.DataTypes is null) throw new JsonException();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            var connected = await SharedSessionAsync(ct) is not null || row.ProtectedToken is not null;
            return WithProgress(new()
            {
                Capabilities = GoogleHealthClient.Capabilities, Connected = connected,
                LastAttempt = row.LastAttempt, LastSync = row.LastSync, NextAttempt = row.NextAttempt,
                ErrorCode = "stored_google_configuration_unreadable"
            });
        }
        var selectedTypes = settings.DataTypes
            .Where(type => GoogleHealthClient.SupportedTypes.Contains(type, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var selectionIsValid = selectedTypes.Length == settings.DataTypes.Length;
        GoogleHealthTokenSession? token;
        try
        {
            token = await StoredSessionAsync(row, ct);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return WithProgress(new()
            {
                Capabilities = GoogleHealthClient.Capabilities, Configured = true, Connected = true,
                ClientId = settings.ClientId, CallbackUrl = settings.CallbackUrl, HistoryDays = settings.HistoryDays, ImportFrom = settings.ImportFrom,
                SelectedTypes = selectedTypes, LastAttempt = row.LastAttempt, LastSync = row.LastSync,
                NextAttempt = row.NextAttempt,
                ErrorCode = "stored_google_configuration_unreadable"
            });
        }
        var storedError = DecodeError(row.ErrorCode);
        return WithProgress(new()
        {
            Capabilities = GoogleHealthClient.Capabilities, Configured = true, Connected = token is not null, ClientId = settings.ClientId,
            CallbackUrl = settings.CallbackUrl, HistoryDays = settings.HistoryDays, ImportFrom = settings.ImportFrom,
            SelectedTypes = selectedTypes, GrantedTypes = GoogleHealthClient.SupportedTypes.Where(t => token?.Scopes.Contains(GoogleHealthClient.ScopeFor(t)) == true).ToArray(),
            AccessTokenExpiresAt = token?.AccessTokenExpiresAt, LastAttempt = row.LastAttempt, LastSync = row.LastSync,
            NextAttempt = row.NextAttempt, ErrorCode = selectionIsValid ? storedError.Code : "unsupported_type",
            ErrorDataTypes = selectionIsValid ? storedError.DataTypes : [], PreviewRequired = settings.PreviewOnly
        });
    }

    public static void ValidateOptions(GoogleHealthOptions options)
    {
        if (options.DataTypes is null || options.DataTypes.Length > 32 || options.DataTypes.Distinct().Count() != options.DataTypes.Length || options.DataTypes.Except(GoogleHealthClient.SupportedTypes).Any())
            throw new GoogleHealthException("unsupported_type");
        if (!GoogleHealthConnectorConfiguration.IsValidClientId(options.ClientId) || options.HistoryDays is < 1 or > 90 ||
            options.ImportFrom is { } importFrom && (importFrom < new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero) || importFrom > DateTimeOffset.UtcNow.AddDays(1)))
            throw new GoogleHealthException("invalid_configuration");
        if (!GoogleHealthConnectorConfiguration.IsValidCallbackUrl(options.CallbackUrl))
            throw new GoogleHealthException("invalid_callback");
    }

    public async Task SaveAsync(GoogleHealthOptions options, Guid subject, CancellationToken ct)
    {
        ValidateOptions(options);
        var gate = coordinator.Gate(db.TenantId); await gate.WaitAsync(ct);
        try
        {
            var row = await Connection(ct);
            if (row is null) { row = new() { Id = Guid.CreateVersion7(), SubjectId = subject }; db.GoogleHealthConnections.Add(row); }
            else
            {
                if (row.SubjectId != subject) throw new GoogleHealthException("connection_owner_required");
                var session = await StoredSessionAsync(row, ct);
                GoogleHealthOptions? prior = null;
                if (session is not null || string.IsNullOrWhiteSpace(options.ClientSecret))
                    prior = await StoredOptionsAsync(row, ct);
                if (session is not null && prior is not null &&
                    (options.ClientId != prior.ClientId || options.CallbackUrl != prior.CallbackUrl))
                    throw new GoogleHealthException("disconnect_first");
                if (string.IsNullOrWhiteSpace(options.ClientSecret))
                {
                    if (options.ClientId == prior?.ClientId) options.ClientSecret = prior.ClientSecret;
                }
            }
            if (string.IsNullOrWhiteSpace(options.ClientSecret)) throw new GoogleHealthException("client_secret_required");
            row.ProtectedSettings = Protect(options); row.ErrorCode = null; row.NextAttempt = null;
            coordinator.Flows.TryRemove(db.TenantId, out _);
            await db.SaveChangesAsync(ct);
            await MirrorOptionsAsync(options, subject, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<GoogleHealthAuthorize> StartAsync(Guid subject, CancellationToken ct)
    {
        var gate = coordinator.Gate(db.TenantId); await gate.WaitAsync(ct);
        try
        {
            var row = await Connection(ct) ?? throw new GoogleHealthException("configure_first");
            if (row.SubjectId != subject) throw new GoogleHealthException("connection_owner_required");
            if (await StoredSessionAsync(row, ct) is not null)
                throw new GoogleHealthException("disconnect_first");
            var settings = await StoredOptionsAsync(row, ct);
            var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
            var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            coordinator.Flows[db.TenantId] = new(
                state,
                verifier,
                subject,
                ConfigurationFingerprint(settings),
                DateTimeOffset.UtcNow.AddMinutes(10));
            var parameters = new Dictionary<string, string?>
            {
                ["client_id"] = settings.ClientId, ["redirect_uri"] = settings.CallbackUrl,
                ["response_type"] = "code", ["access_type"] = "offline", ["prompt"] = "consent select_account",
                ["scope"] = "openid " + string.Join(' ', GoogleHealthClient.SupportedTypes.Select(GoogleHealthClient.ScopeFor).Distinct()),
                ["state"] = state, ["code_challenge_method"] = "S256",
                ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            };
            return new() { Url = QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", parameters) };
        }
        finally { gate.Release(); }
    }

    public async Task CompleteAsync(GoogleHealthCallback callback, Guid subject, CancellationToken ct)
    {
        var gate = coordinator.Gate(db.TenantId); await gate.WaitAsync(ct);
        try
        {
            if (!coordinator.Flows.TryGetValue(db.TenantId, out var flow) || flow.Expires <= DateTimeOffset.UtcNow || flow.SubjectId != subject || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(flow.State), Encoding.UTF8.GetBytes(callback.State)))
                throw new GoogleHealthException("expired_signin");
            coordinator.Flows.TryRemove(db.TenantId, out _);
            var row = await Connection(ct) ?? throw new GoogleHealthException("configure_first");
            var settings = await StoredOptionsAsync(row, ct);
            if (row.SubjectId != subject ||
                ConfigurationFingerprint(settings) != flow.Settings)
                throw new GoogleHealthException("expired_signin");
            var requestedScopes = GoogleHealthClient.SupportedTypes.Select(GoogleHealthClient.ScopeFor).Append("openid")
                .Distinct(StringComparer.Ordinal).ToArray();
            var token = await oauth.ExchangeAuthorizationCodeAsync(
                ConnectorConfiguration(settings),
                callback.Code,
                flow.Verifier,
                requestedScopes,
                ct);
            var account = await oauth.AccountKeyAsync(token.AccessToken!, ct);
            if (row.AccountKey is not null && row.AccountKey != account)
            {
                await oauth.RevokeAsync(token.RefreshToken, ct);
                throw new GoogleHealthException("account_mismatch");
            }
            var missingScopes = GoogleHealthClient.SupportedTypes
                .Where(type => !token.Scopes.Contains(GoogleHealthClient.ScopeFor(type), StringComparer.Ordinal)).ToArray();
            row.AccountKey = account;
            row.ProtectedToken = Protect(token);
            row.NextAttempt = null; row.LastAttempt = null;
            row.ErrorCode = missingScopes.Length == 0 ? null : EncodeError("partial_consent", missingScopes);
            await db.SaveChangesAsync(ct);
            await MirrorTokenAsync(settings, token, subject, ct);
            await oauth.StoreSessionAsync(token);
        }
        catch (Exception ex) when (ex is GoogleHealthException or HttpRequestException or JsonException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            var error = ex as GoogleHealthException ?? new GoogleHealthException(
                ex is JsonException ? "invalid_google_response" : "google_unavailable",
                stage: ex is JsonException ? "token_response" : "network");
            LogFailure(error);
            db.ChangeTracker.Clear();
            var row = await Connection(CancellationToken.None);
            if (row is not null && row.ProtectedToken is null)
            {
                row.LastAttempt = DateTimeOffset.UtcNow;
                row.NextAttempt = null;
                row.ErrorCode = EncodeError(error.Message, error.DataType is null ? null : [error.DataType]);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            throw error;
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(Guid subject, CancellationToken ct)
    {
        var gate = coordinator.Gate(db.TenantId); await gate.WaitAsync(ct);
        try
        {
            var row = await Connection(ct);
            if (row is null) return;
            if (row.SubjectId != subject) throw new GoogleHealthException("connection_owner_required");
            GoogleHealthTokenSession? token = null;
            var revokeFailed = false;
            try { token = await StoredSessionAsync(row, ct); }
            catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
            {
                revokeFailed = true;
            }
            row.ProtectedToken = null; row.ErrorCode = revokeFailed ? "revoke_in_google" : null; row.NextAttempt = null;
            coordinator.Flows.TryRemove(db.TenantId, out _);
            await db.SaveChangesAsync(ct);
            oauth.InvalidateToken();
            if (token is not null)
            {
                try { if (!await oauth.RevokeAsync(token.RefreshToken, ct)) row.ErrorCode = "revoke_in_google"; }
                catch (HttpRequestException) { row.ErrorCode = "revoke_in_google"; }
                catch (TaskCanceledException) { row.ErrorCode = "revoke_in_google"; }
                await db.SaveChangesAsync(CancellationToken.None);
            }
            await ClearMirroredTokenAsync(subject, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    public async Task PurgeAsync(Guid subject, CancellationToken ct)
    {
        var gate = coordinator.Gate(db.TenantId); await gate.WaitAsync(ct);
        try
        {
            var row = await Connection(ct);
            if (row is null) return;
            if (row.SubjectId != subject) throw new GoogleHealthException("connection_owner_required");
            if (await StoredSessionAsync(row, ct) is not null)
                throw new GoogleHealthException("disconnect_first");
            if (writer is not null) await writer.PurgeAsync(ct);
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.GoogleHealthReadings.ExecuteDeleteAsync(ct);
                row.AccountKey = null; row.LastSync = null;
                await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            });
        }
        finally { gate.Release(); }
    }

    public async Task<GoogleHealthPreview> PreviewAsync(Guid subject, CancellationToken ct)
    {
        var gate = coordinator.Gate(db.TenantId); await gate.WaitAsync(ct);
        try
        {
            var row = await Connection(ct) ?? throw new GoogleHealthException("configure_first");
            if (row.SubjectId != subject) throw new GoogleHealthException("connection_owner_required");
            var settings = await StoredOptionsAsync(row, ct);
            var token = await StoredSessionAsync(row, ct) ??
                throw new GoogleHealthException("configure_first");
            var now = DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(token.AccessToken) || token.AccessTokenExpiresAt is null ||
                token.AccessTokenExpiresAt <= now.Add(AccessTokenSafety))
            {
                token = await RefreshSessionAsync(settings, token, ct);
                row.ProtectedToken = Protect(token);
                await db.SaveChangesAsync(ct);
                await MirrorTokenAsync(settings, token, row.SubjectId, ct);
            }
            var from = settings.ImportFrom ?? now.AddDays(-settings.HistoryDays);
            var items = new List<GoogleHealthPreviewItem>();
            foreach (var capability in GoogleHealthClient.Capabilities)
            {
                var type = capability.DataType;
                var granted = token.Scopes.Contains(GoogleHealthClient.ScopeFor(type), StringComparer.Ordinal);
                if (!granted)
                {
                    items.Add(new() { DataType = type, Granted = false, Supported = capability.Supported });
                    continue;
                }
                try
                {
                    var count = await google.CountAsync(token.AccessToken!, type, from, now, ct);
                    items.Add(new() { DataType = type, Granted = true, Count = count, Supported = capability.Supported });
                }
                catch (GoogleHealthException ex)
                {
                    items.Add(new() { DataType = type, Granted = true, ErrorCode = ex.Message, Supported = capability.Supported });
                }
            }
            return new() { Items = items.ToArray() };
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            throw new GoogleHealthException("stored_google_configuration_unreadable", stage: "preview");
        }
        finally { gate.Release(); }
    }

    public async Task QueueSyncAsync(CancellationToken ct)
    {
        var row = await Connection(ct) ?? throw new GoogleHealthException("configure_first");
        try
        {
            var settings = await StoredOptionsAsync(row, ct);
            if (await StoredSessionAsync(row, ct) is null)
                throw new GoogleHealthException("configure_first");
            if (settings.PreviewOnly) throw new GoogleHealthException("preview_required");
            if (settings.DataTypes.Length == 0) throw new GoogleHealthException("no_types_selected");
            coordinator.Queue(db.TenantId, settings.DataTypes.Length);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            throw new GoogleHealthException("stored_google_configuration_unreadable", stage: "sync_queue");
        }
    }

    public async Task SyncAsync(bool force, CancellationToken ct)
    {
        var gate = coordinator.Gate(db.TenantId);
        if (force) await gate.WaitAsync(ct);
        else if (!await gate.WaitAsync(0, ct)) return;
        var stage = "connection_read";
        try
        {
            var row = await Connection(ct);
            if (row is null || row.NextAttempt > DateTimeOffset.UtcNow || (!force && row.LastAttempt > DateTimeOffset.UtcNow.AddMinutes(-15))) return;
            row.LastAttempt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            try
            {
                stage = "session_read";
                GoogleHealthOptions settings;
                GoogleHealthTokenSession token;
                try
                {
                    settings = await StoredOptionsAsync(row, ct);
                    var storedToken = await StoredSessionAsync(row, ct);
                    if (storedToken is null) return;
                    token = storedToken;
                    if (settings.DataTypes is null)
                        throw new JsonException();
                }
                catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
                {
                    throw new GoogleHealthException("stored_google_configuration_unreadable", stage: stage);
                }
                if (settings.PreviewOnly || settings.DataTypes.Length == 0) return;
                var now = DateTimeOffset.UtcNow;
                var access = token.AccessToken ?? "";
                if (string.IsNullOrWhiteSpace(access) || token.AccessTokenExpiresAt is null ||
                    token.AccessTokenExpiresAt <= now.Add(AccessTokenSafety))
                {
                    stage = "token_refresh";
                    coordinator.Report(db.TenantId, GoogleHealthSyncPhase.RefreshingSession);
                    token = await RefreshSessionAsync(settings, token, ct);
                    access = token.AccessToken!;
                    row.ProtectedToken = Protect(token);
                    await db.SaveChangesAsync(ct);
                    await MirrorTokenAsync(settings, token, row.SubjectId, ct);
                }
                stage = "scope_validation";
                var active = settings.DataTypes.Where(t => token.Scopes.Contains(GoogleHealthClient.ScopeFor(t))).ToArray();
                if (active.Length == 0) throw new GoogleHealthException("permission_denied", stage: "scope_validation");
                coordinator.Report(db.TenantId, GoogleHealthSyncPhase.Reading, totalDataTypes: active.Length);
                var to = DateTimeOffset.UtcNow; var from = settings.ImportFrom ?? to.AddDays(-settings.HistoryDays);
                async Task<(List<GoogleHealthReading> Readings, List<Nocturne.Core.Models.SleepSession> SleepSessions)> ReadAllAsync()
                {
                    var result = new List<GoogleHealthReading>();
                    var sleepSessions = new List<Nocturne.Core.Models.SleepSession>();
                    for (var index = 0; index < active.Length; index++)
                    {
                        var type = active[index];
                        coordinator.Report(db.TenantId, GoogleHealthSyncPhase.Reading, type, index, active.Length, 0);
                        void PageRead(int pages) => coordinator.Report(db.TenantId, GoogleHealthSyncPhase.Reading, type, index, active.Length, pages);
                        if (type == "sleep") sleepSessions.AddRange(await google.ReadSleepAsync(access, from, to, ct, PageRead));
                        else result.AddRange(await google.ReadAsync(access, type, from, to, ct, PageRead));
                        coordinator.Report(db.TenantId, GoogleHealthSyncPhase.Reading, type, index + 1, active.Length);
                    }
                    return (result, sleepSessions);
                }
                List<GoogleHealthReading> readings;
                List<Nocturne.Core.Models.SleepSession> sleepSessions;
                try
                {
                    stage = "google_read";
                    (readings, sleepSessions) = await ReadAllAsync();
                }
                catch (GoogleHealthException first) when (first.Message == "access_token_rejected")
                {
                    logger?.LogInformation(
                        "Google Health access token was rejected early for tenant {TenantId}, data type {DataType}; refreshing the session once",
                        db.TenantId, first.DataType);
                    stage = "token_refresh";
                    coordinator.Report(db.TenantId, GoogleHealthSyncPhase.RefreshingSession);
                    token = await RefreshSessionAsync(settings, token, ct, forceRefresh: true);
                    access = token.AccessToken!;
                    row.ProtectedToken = Protect(token);
                    await db.SaveChangesAsync(ct);
                    await MirrorTokenAsync(settings, token, row.SubjectId, ct);
                    try
                    {
                        stage = "google_read";
                        (readings, sleepSessions) = await ReadAllAsync();
                    }
                    catch (GoogleHealthException second) when (second.Message == "access_token_rejected")
                    {
                        throw new GoogleHealthException("reconnect_required", stage: second.Stage,
                            dataType: second.DataType, providerReason: second.ProviderReason,
                            providerStatus: second.ProviderStatus);
                    }
                }
                stage = "data_validation";
                coordinator.Report(db.TenantId, GoogleHealthSyncPhase.Validating);
                if (readings.Select(GoogleHealthClient.Key).Distinct().Count() != readings.Count)
                    throw new GoogleHealthException("duplicate_google_data", stage: "data_validation");
                if (sleepSessions.Select(session => session.OriginalId).Distinct(StringComparer.Ordinal).Count() != sleepSessions.Count)
                    throw new GoogleHealthException("duplicate_google_data", stage: "data_validation", dataType: "sleep");
                // Replace only the completely fetched window, so retries, edits and source deletions cannot double-count steps.
                var firstMills = from.ToUnixTimeMilliseconds(); var lastMills = to.ToUnixTimeMilliseconds();
                var replacements = readings.Select(r => new GoogleHealthReadingEntity
                {
                    Id = Guid.CreateVersion7(), DataType = r.DataType, SourceKey = GoogleHealthClient.Key(r), Mills = r.Mills,
                    EndMills = r.EndMills, UtcOffsetMinutes = r.UtcOffsetMinutes, Value = r.Value, Unit = r.Unit
                }).ToArray();
                var missingConsent = settings.DataTypes.Except(active, StringComparer.Ordinal).ToArray();
                var strategy = db.Database.CreateExecutionStrategy();
                stage = "database_write";
                coordinator.Report(db.TenantId, GoogleHealthSyncPhase.Saving);
                await strategy.ExecuteAsync(async () =>
                {
                    foreach (var replacement in replacements)
                    {
                        var entry = db.Entry(replacement);
                        if (entry.State != EntityState.Detached) entry.State = EntityState.Detached;
                    }
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    await db.GoogleHealthReadings.Where(x => active.Contains(x.DataType) && x.Mills >= firstMills && x.Mills < lastMills).ExecuteDeleteAsync(ct);
                    db.GoogleHealthReadings.AddRange(replacements);
                    row.LastSync = to; row.NextAttempt = null;
                    row.ErrorCode = missingConsent.Length > 0
                        ? EncodeError("partial_consent", missingConsent)
                        : null;
                    await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
                });
                stage = "native_write";
                coordinator.Report(db.TenantId, GoogleHealthSyncPhase.Integrating);
                if (writer is not null) await writer.WriteAsync(readings, sleepSessions, active, from, to, ct);
                stage = "complete";
            }
            catch (Exception ex) when (ex is GoogleHealthException or HttpRequestException or JsonException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                var error = ex as GoogleHealthException ?? new GoogleHealthException(
                    ex is JsonException ? "invalid_google_response" : "google_unavailable",
                    stage: ex is JsonException ? "response_parse" : "network");
                LogFailure(error);
                db.ChangeTracker.Clear();
                row = await Connection(ct);
                if (row is not null)
                {
                    row.ErrorCode = EncodeError(error.Message, error.DataType is null ? null : [error.DataType]);
                    row.NextAttempt = null;
                    if (error.RetryAfter is { } delay && delay > TimeSpan.Zero)
                        row.NextAttempt = DateTimeOffset.UtcNow.Add(delay > TimeSpan.FromDays(7) ? TimeSpan.FromDays(7) : delay);
                    if (error.Message == "reconnect_required")
                    {
                        row.ProtectedToken = null;
                        oauth.InvalidateToken();
                        await ClearMirroredTokenAsync(row.SubjectId, ct);
                    }
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var diagnosticId = Guid.NewGuid().ToString("N")[..12];
                var errorCode = $"internal_sync_{stage}";
                logger?.LogError(ex,
                    "Unexpected Google Health import failure for tenant {TenantId}; diagnostic {DiagnosticId}, stage {Stage}, error code {Code}",
                    db.TenantId, diagnosticId, stage, errorCode);
                db.ChangeTracker.Clear();
                row = await Connection(CancellationToken.None);
                if (row is not null)
                {
                    row.ErrorCode = errorCode;
                    row.NextAttempt = null;
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            }
        }
        finally { gate.Release(); }
    }
}
