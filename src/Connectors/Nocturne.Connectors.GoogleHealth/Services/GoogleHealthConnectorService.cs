using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Health;

namespace Nocturne.Connectors.GoogleHealth.Services;

public sealed class GoogleHealthConnectorService(
    HttpClient httpClient,
    IConnectorServerResolver<GoogleHealthConnectorConfiguration> serverResolver,
    GoogleHealthClient google,
    GoogleHealthAuthTokenProvider oauth,
    IGoogleHealthReadingWriter writer,
    IGoogleHealthSyncCoordinator coordinator,
    IConnectorConfigurationService connectorConfigurations,
    ITenantAccessor tenantAccessor,
    ILogger<GoogleHealthConnectorService> logger,
    IConnectorPublisher? publisher = null)
    : BaseConnectorService<GoogleHealthConnectorConfiguration>(httpClient, serverResolver, logger, publisher)
{
    private const string ConnectorName = "GoogleHealth";

    protected override string ConnectorSource => DataSources.GoogleHealthConnector;
    public override string ServiceName => ServiceNames.GoogleHealthConnector;
    protected override DateTime? InitialSyncFloor => null;

    public override Task<SyncResult> SyncDataAsync(
        GoogleHealthConnectorConfiguration config,
        CancellationToken cancellationToken = default,
        DateTime? since = null,
        ISyncProgressReporter? progressReporter = null) =>
        base.SyncDataAsync(
            config,
            cancellationToken,
            since ?? DateTime.UtcNow.AddDays(-config.HistoryDays),
            progressReporter);

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        GoogleHealthConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow };
        var tenantId = tenantAccessor.TenantId;
        var gate = coordinator.Gate(tenantId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var selected = ResolveActiveTypes(request, config)
                .Select(TypeName)
                .ToArray();
            if (config.PreviewOnly || selected.Length == 0)
                return Complete(result);

            var session = await SessionAsync(config, cancellationToken);
            var active = selected
                .Where(type => session.Scopes.Contains(GoogleHealthClient.ScopeFor(type), StringComparer.Ordinal))
                .ToArray();
            if (active.Length == 0)
                throw new GoogleHealthException("permission_denied", stage: "scope_validation");

            var to = request.To is { } requestedTo
                ? new DateTimeOffset(DateTime.SpecifyKind(requestedTo, DateTimeKind.Utc))
                : DateTimeOffset.UtcNow;
            var from = request.From is { } requestedFrom
                ? new DateTimeOffset(DateTime.SpecifyKind(requestedFrom, DateTimeKind.Utc))
                : ImportFrom(config, to);

            coordinator.Report(tenantId, GoogleHealthSyncPhase.Reading, totalDataTypes: active.Length);
            var (readings, sleepSessions) = await ReadWithRefreshAsync(
                config, session.AccessToken!, active, from, to, tenantId, cancellationToken);

            coordinator.Report(tenantId, GoogleHealthSyncPhase.Validating);
            readings = readings.DistinctBy(GoogleHealthClient.Key).ToList();
            sleepSessions = sleepSessions.DistinctBy(session => session.OriginalId, StringComparer.Ordinal).ToList();
            coordinator.Report(tenantId, GoogleHealthSyncPhase.Integrating);
            await writer.WriteAsync(readings, sleepSessions, active, from, to, config.BatchSize, cancellationToken);

            AddCounts(result, readings, sleepSessions);
            if (request.From is null && !string.IsNullOrWhiteSpace(config.ImportFrom))
                await ConsumeImportFromAsync(cancellationToken);
            var missingConsent = selected.Except(active, StringComparer.Ordinal).ToArray();
            return Complete(result, missingConsent.Length == 0
                ? string.Empty
                : GoogleHealthErrorCode.Encode("partial_consent", missingConsent));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is GoogleHealthException or HttpRequestException or JsonException or TaskCanceledException)
        {
            var error = ex as GoogleHealthException ?? new GoogleHealthException(
                ex is JsonException ? "invalid_google_response" : "google_unavailable",
                stage: ex is JsonException ? "response_parse" : "network");
            LogFailure(error, tenantId);
            if (error.Message == "reconnect_required")
                await ClearSessionAsync(cancellationToken);
            return Fail(result, GoogleHealthErrorCode.Encode(
                error.Message,
                error.DataType is null ? null : [error.DataType]));
        }
        catch (Exception ex)
        {
            var diagnosticId = Guid.NewGuid().ToString("N")[..12];
            logger.LogError(ex,
                "Unexpected Google Health import failure for tenant {TenantId}; diagnostic {DiagnosticId}",
                tenantId, diagnosticId);
            return Fail(result, "internal_sync");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<GoogleHealthTokenSession> SessionAsync(
        GoogleHealthConnectorConfiguration config,
        CancellationToken ct,
        bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(config.RefreshToken))
            throw new GoogleHealthException("reconnect_required", stage: "session_read");

        var scopes = (config.GrantedScopes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await oauth.SeedSessionAsync(new GoogleHealthTokenSession(config.RefreshToken, scopes));
        if (forceRefresh)
            oauth.InvalidateToken();

        var accessToken = await oauth.GetValidTokenAsync(config, ct);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new GoogleHealthException("invalid_token_response", stage: "token_refresh");

        var session = await oauth.GetCurrentSessionAsync() ??
            throw new GoogleHealthException("invalid_token_response", stage: "token_cache");
        await PersistSessionAsync(session, ct);
        return session;
    }

    private async Task<(List<GoogleHealthReading> Readings, List<Nocturne.Core.Models.SleepSession> SleepSessions)>
        ReadWithRefreshAsync(
            GoogleHealthConnectorConfiguration config,
            string accessToken,
            string[] active,
            DateTimeOffset from,
            DateTimeOffset to,
            Guid tenantId,
            CancellationToken ct)
    {
        try
        {
            return await ReadOnceAsync(accessToken, active, from, to, tenantId, ct);
        }
        catch (GoogleHealthException first) when (first.Message == "access_token_rejected")
        {
            logger.LogInformation(
                "Google Health access token was rejected for tenant {TenantId}; refreshing once",
                tenantId);
            coordinator.Report(tenantId, GoogleHealthSyncPhase.RefreshingSession);
            var refreshed = await SessionAsync(config, ct, forceRefresh: true);
            try
            {
                return await ReadOnceAsync(refreshed.AccessToken!, active, from, to, tenantId, ct);
            }
            catch (GoogleHealthException second) when (second.Message == "access_token_rejected")
            {
                throw new GoogleHealthException("reconnect_required", stage: second.Stage,
                    dataType: second.DataType, providerReason: second.ProviderReason,
                    providerStatus: second.ProviderStatus);
            }
        }
    }

    private async Task<(List<GoogleHealthReading> Readings, List<Nocturne.Core.Models.SleepSession> SleepSessions)>
        ReadOnceAsync(
            string accessToken,
            string[] active,
            DateTimeOffset from,
            DateTimeOffset to,
            Guid tenantId,
            CancellationToken ct)
    {
        var readings = new List<GoogleHealthReading>();
        var sleepSessions = new List<Nocturne.Core.Models.SleepSession>();
        for (var index = 0; index < active.Length; index++)
        {
            var type = active[index];
            coordinator.Report(tenantId, GoogleHealthSyncPhase.Reading, type, index, active.Length, 0);
            void PageRead(int pages) =>
                coordinator.Report(tenantId, GoogleHealthSyncPhase.Reading, type, index, active.Length, pages);
            if (type == "sleep")
                sleepSessions.AddRange(await google.ReadSleepAsync(accessToken, from, to, ct, PageRead));
            else
                readings.AddRange(await google.ReadAsync(accessToken, type, from, to, ct, PageRead));
            coordinator.Report(tenantId, GoogleHealthSyncPhase.Reading, type, index + 1, active.Length);
        }
        return (readings, sleepSessions);
    }

    private async Task PersistSessionAsync(GoogleHealthTokenSession session, CancellationToken ct)
    {
        var secrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
        var scopes = string.Join(' ', session.Scopes.Distinct(StringComparer.Ordinal));
        if (secrets.GetValueOrDefault("refreshToken") == session.RefreshToken &&
            secrets.GetValueOrDefault("grantedScopes") == scopes)
            return;
        secrets["refreshToken"] = session.RefreshToken;
        secrets["grantedScopes"] = scopes;
        await connectorConfigurations.SaveSecretsAsync(ConnectorName, secrets, ct: ct);
    }

    private async Task ClearSessionAsync(CancellationToken ct)
    {
        oauth.InvalidateToken();
        var secrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
        secrets.Remove("refreshToken");
        secrets.Remove("grantedScopes");
        await connectorConfigurations.SaveSecretsAsync(ConnectorName, secrets, ct: ct);
    }

    private async Task ConsumeImportFromAsync(CancellationToken ct)
    {
        var stored = await connectorConfigurations.GetConfigurationAsync(ConnectorName, ct);
        if (stored is null) return;
        using var document = JsonDocument.Parse(stored.Configuration.RootElement.GetRawText());
        var configuration = document.RootElement.Deserialize<Dictionary<string, JsonElement>>() ?? [];
        configuration["importFrom"] = JsonSerializer.SerializeToElement<string?>(null);
        using var updated = JsonSerializer.SerializeToDocument(configuration);
        await connectorConfigurations.SaveConfigurationAsync(ConnectorName, updated, ct: ct);
    }

    private static DateTimeOffset ImportFrom(GoogleHealthConnectorConfiguration config, DateTimeOffset to) =>
        string.IsNullOrWhiteSpace(config.ImportFrom)
            ? to.AddDays(-config.HistoryDays)
            : DateTimeOffset.Parse(config.ImportFrom, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string TypeName(SyncDataType type) => type switch
    {
        SyncDataType.Steps => "steps",
        SyncDataType.HeartRate => "heart-rate",
        SyncDataType.BodyWeight => "weight",
        SyncDataType.Sleep => "sleep",
        _ => throw new GoogleHealthException("unsupported_type")
    };

    private static void AddCounts(
        SyncResult result,
        IReadOnlyCollection<GoogleHealthReading> readings,
        IReadOnlyCollection<Nocturne.Core.Models.SleepSession> sleepSessions)
    {
        result.ItemsSynced[SyncDataType.Steps] = readings.Count(item => item.DataType == "steps");
        result.ItemsSynced[SyncDataType.HeartRate] = readings.Count(item => item.DataType == "heart-rate");
        result.ItemsSynced[SyncDataType.BodyWeight] = readings.Count(item => item.DataType == "weight");
        result.ItemsSynced[SyncDataType.Sleep] = sleepSessions.Count;
    }

    private static SyncResult Complete(SyncResult result, string message = "")
    {
        result.Success = true;
        result.Message = message;
        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    private static SyncResult Fail(SyncResult result, string code)
    {
        result.Success = false;
        result.Message = code;
        result.Errors.Add(code);
        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    private void LogFailure(GoogleHealthException error, Guid tenantId) => logger.LogWarning(
        "Google Health import failed for tenant {TenantId} with code {Code} at stage {Stage} for data type {DataType}; provider status {ProviderStatus}, provider reason {ProviderReason}",
        tenantId, error.Message, error.Stage, error.DataType, error.ProviderStatus, error.ProviderReason);
}

public static class GoogleHealthErrorCode
{
    public static string Encode(string code, IEnumerable<string>? dataTypes = null)
    {
        var types = dataTypes?
            .Where(type => GoogleHealthClient.SupportedTypes.Contains(type, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        return types.Length == 0 ? code : $"{code}:{string.Join(',', types)}";
    }

    public static (string? Code, string[] DataTypes) Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, []);
        var separator = value.IndexOf(':');
        if (separator < 0) return (value, []);
        return (value[..separator], value[(separator + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(type => GoogleHealthClient.SupportedTypes.Contains(type, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray());
    }
}
