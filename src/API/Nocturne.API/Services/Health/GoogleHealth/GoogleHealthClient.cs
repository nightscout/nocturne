using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Health;

namespace Nocturne.API.Services.Health.GoogleHealth;

public sealed class GoogleHealthException(
    string code,
    TimeSpan? retryAfter = null,
    string? stage = null,
    string? dataType = null,
    string? providerReason = null,
    int? providerStatus = null) : Exception(code)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public string? Stage { get; } = stage;
    public string? DataType { get; } = dataType;
    public string? ProviderReason { get; } = providerReason;
    public int? ProviderStatus { get; } = providerStatus;
}

public sealed class GoogleHealthClient(HttpClient http)
{
    // A faulty provider can keep issuing unique cursors that bypass cycle detection.
    internal const int MaximumHistoryPages = 10_000;

    public const string ActivityScope = "https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly";
    public const string MetricsScope = "https://www.googleapis.com/auth/googlehealth.health_metrics_and_measurements.readonly";
    public const string SleepScope = "https://www.googleapis.com/auth/googlehealth.sleep.readonly";
    public static readonly GoogleHealthCapability[] Capabilities =
    [
        new() { DataType = "steps", Supported = true, Destination = "step-counts" },
        new() { DataType = "heart-rate", Supported = true, Destination = "heart-rates" },
        new() { DataType = "weight", Supported = true, Destination = "body-weights" },
        new() { DataType = "sleep", Supported = true, Destination = "sleep-sessions" }, new() { DataType = "body-fat" },
        new() { DataType = "distance" }, new() { DataType = "oxygen-saturation" }, new() { DataType = "heart-rate-variability" }
    ];
    public static string[] SupportedTypes => Capabilities.Where(c => c.Supported).Select(c => c.DataType).ToArray();
    public static string ScopeFor(string type) => type switch
    {
        "steps" => ActivityScope,
        "heart-rate" or "weight" or "body-fat" or "oxygen-saturation" or "heart-rate-variability" => MetricsScope,
        "distance" => ActivityScope,
        "sleep" => SleepScope,
        _ => throw new GoogleHealthException("unsupported_type")
    };

    public async Task<int> CountAsync(string token, string type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var field = type switch
        {
            "steps" or "distance" => $"{type.Replace('-', '_')}.interval.start_time",
            "sleep" => "sleep.interval.start_time",
            _ => $"{type.Replace('-', '_')}.sample_time.physical_time"
        };
        var filter = $"{field} >= \"{from.UtcDateTime:O}\" AND {field} < \"{to.UtcDateTime:O}\"";
        var pageSize = type == "sleep" ? 25 : 10000;
        var root = $"https://health.googleapis.com/v4/users/me/dataTypes/{type}/dataPoints:reconcile?pageSize={pageSize}&filter={Uri.EscapeDataString(filter)}";
        var count = 0;
        var pageToken = "";
        var seen = new HashSet<string>();
        for (var page = 0; page < MaximumHistoryPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = pageToken.Length == 0 ? root : root + "&pageToken=" + Uri.EscapeDataString(pageToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw await ErrorAsync(response, response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "access_token_rejected",
                    System.Net.HttpStatusCode.Forbidden => "permission_denied",
                    System.Net.HttpStatusCode.BadRequest => "invalid_google_request",
                    System.Net.HttpStatusCode.NotFound => "google_resource_not_found",
                    _ => "google_unavailable"
                }, type, ct);
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (json.RootElement.TryGetProperty("dataPoints", out var data) && data.ValueKind == JsonValueKind.Array)
                    count += data.GetArrayLength();
                pageToken = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() ?? "" : "";
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new GoogleHealthException("invalid_google_response", stage: "inventory", dataType: type);
            }
            if (pageToken.Length == 0) return count;
            if (!seen.Add(pageToken)) throw new GoogleHealthException("pagination_failed", stage: "inventory", dataType: type);
        }
        throw new GoogleHealthException("history_too_large", stage: "inventory", dataType: type);
    }

    public async Task<List<SleepSession>> ReadSleepAsync(
        string token, DateTimeOffset from, DateTimeOffset to, CancellationToken ct,
        Action<int>? onPageRead = null)
    {
        var filter = $"sleep.interval.start_time >= \"{from.UtcDateTime:O}\" AND sleep.interval.start_time < \"{to.UtcDateTime:O}\"";
        var root = $"https://health.googleapis.com/v4/users/me/dataTypes/sleep/dataPoints:reconcile?pageSize=25&filter={Uri.EscapeDataString(filter)}";
        var sessions = new List<SleepSession>();
        var seen = new HashSet<string>();
        var pageToken = "";
        for (var page = 0; page < MaximumHistoryPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = pageToken.Length == 0 ? root : root + "&pageToken=" + Uri.EscapeDataString(pageToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw await ErrorAsync(response, response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "access_token_rejected",
                    System.Net.HttpStatusCode.Forbidden => "permission_denied",
                    System.Net.HttpStatusCode.BadRequest => "invalid_google_request",
                    System.Net.HttpStatusCode.NotFound => "google_resource_not_found",
                    _ => "google_unavailable"
                }, "sleep", ct);
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (json.RootElement.TryGetProperty("dataPoints", out var data))
                    foreach (var item in data.EnumerateArray())
                    {
                        var session = ParseSleep(item);
                        if (session.StartMills < from.ToUnixTimeMilliseconds() || session.StartMills >= to.ToUnixTimeMilliseconds())
                            throw new GoogleHealthException("unexpected_time_range", stage: "data_parse", dataType: "sleep");
                        sessions.Add(session);
                    }
                pageToken = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() ?? "" : "";
                onPageRead?.Invoke(page + 1);
            }
            catch (GoogleHealthException) { throw; }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new GoogleHealthException("invalid_google_response", stage: "data_read", dataType: "sleep");
            }
            if (pageToken.Length == 0) return sessions;
            if (!seen.Add(pageToken)) throw new GoogleHealthException("pagination_failed", stage: "data_read", dataType: "sleep");
        }
        throw new GoogleHealthException("history_too_large", stage: "data_read", dataType: "sleep");
    }

    public Task<JsonElement> ExchangeAuthorizationCodeAsync(Dictionary<string, string> form, CancellationToken ct) =>
        ExchangeAsync(form, "expired_signin", "authorization_code", ct);

    public Task<JsonElement> RefreshAccessTokenAsync(Dictionary<string, string> form, CancellationToken ct) =>
        ExchangeAsync(form, "reconnect_required", "token_refresh", ct);

    private async Task<JsonElement> ExchangeAsync(
        Dictionary<string, string> form, string invalidGrantCode, string stage, CancellationToken ct)
    {
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
            throw await OAuthErrorAsync(response, invalidGrantCode, stage, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.Clone();
    }

    public async Task<bool> RevokeAsync(string refreshToken, CancellationToken ct)
    {
        using var response = await http.PostAsync("https://oauth2.googleapis.com/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken }), ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<string> AccountKeyAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new GoogleHealthException("reconnect_required", stage: "account_identity", providerStatus: (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("sub", out var subjectValue) || subjectValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(subjectValue.GetString()))
            throw new GoogleHealthException("invalid_token_response", stage: "account_identity");
        var subject = subjectValue.GetString()!;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject)));
    }

    public async Task<List<GoogleHealthReading>> ReadAsync(
        string token, string type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct,
        Action<int>? onPageRead = null)
    {
        var field = type == "steps" ? "steps.interval.start_time" : $"{type.Replace('-', '_')}.sample_time.physical_time";
        var filter = $"{field} >= \"{from.UtcDateTime:O}\" AND {field} < \"{to.UtcDateTime:O}\"";
        var root = $"https://health.googleapis.com/v4/users/me/dataTypes/{type}/dataPoints:reconcile?pageSize=10000&filter={Uri.EscapeDataString(filter)}";
        var points = new List<GoogleHealthReading>();
        var seen = new HashSet<string>();
        var pageToken = "";
        for (var page = 0; page < MaximumHistoryPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = pageToken.Length == 0 ? root : root + "&pageToken=" + Uri.EscapeDataString(pageToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw await ErrorAsync(response, response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "access_token_rejected",
                    System.Net.HttpStatusCode.Forbidden => "permission_denied",
                    System.Net.HttpStatusCode.BadRequest => "invalid_google_request",
                    System.Net.HttpStatusCode.NotFound => "google_resource_not_found",
                    _ => "google_unavailable"
                }, type, ct);
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (json.RootElement.TryGetProperty("dataPoints", out var data))
                    foreach (var item in data.EnumerateArray())
                    {
                        var point = Parse(type, item);
                        if (point.Mills < from.ToUnixTimeMilliseconds() || point.Mills >= to.ToUnixTimeMilliseconds())
                            throw new GoogleHealthException("unexpected_time_range", stage: "data_parse", dataType: type);
                        points.Add(point);
                    }
                pageToken = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() ?? "" : "";
                onPageRead?.Invoke(page + 1);
            }
            catch (GoogleHealthException)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new GoogleHealthException("invalid_google_response", stage: "data_read", dataType: type);
            }
            if (pageToken.Length == 0) return points;
            if (!seen.Add(pageToken)) throw new GoogleHealthException("pagination_failed", stage: "data_read", dataType: type);
        }
        throw new GoogleHealthException("history_too_large", stage: "data_read", dataType: type);
    }

    private static async Task<GoogleHealthException> OAuthErrorAsync(
        HttpResponseMessage response, string invalidGrantCode, string stage, CancellationToken ct)
    {
        var code = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "rate_limited" : "google_unavailable";
        string? providerReason = null;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            string? rawReason = null;
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                rawReason = error.GetString();
            providerReason = SafeProviderReason(rawReason);
            code = rawReason switch
            {
                "invalid_grant" => invalidGrantCode,
                "invalid_client" => "invalid_client_credentials",
                "redirect_uri_mismatch" => "invalid_callback",
                "invalid_scope" => "oauth_scope_configuration",
                "access_denied" => "permission_denied",
                "invalid_request" => "oauth_request_invalid",
                "temporarily_unavailable" or "server_error" => "google_unavailable",
                _ => code
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }
        return new GoogleHealthException(code, RetryAfter(response), stage, providerReason: providerReason,
            providerStatus: (int)response.StatusCode);
    }

    private static async Task<GoogleHealthException> ErrorAsync(
        HttpResponseMessage response, string fallback, string dataType, CancellationToken ct)
    {
        var code = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "rate_limited" : fallback;
        string? providerReason = null;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                var reasons = GoogleReasons(details);
                (code, providerReason) = MapGoogleReason(reasons, code);
                providerReason = SafeProviderReason(providerReason);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }
        return new GoogleHealthException(code, RetryAfter(response), "data_read", dataType, providerReason,
            (int)response.StatusCode);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);

    private static string? SafeProviderReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) && reason.Length <= 100 &&
        reason.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or >= 'a' and <= 'z')
            ? reason
            : null;

    private static (string Code, string? Reason) MapGoogleReason(HashSet<string> reasons, string fallback)
    {
        string? Find(string reason) => reasons.FirstOrDefault(value => value.Equals(reason, StringComparison.OrdinalIgnoreCase));
        string? FindPrefix(string prefix) => reasons.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var reason = Find("ACCOUNT_NOT_LINKED");
        if (reason is not null) return ("account_not_linked", reason);
        reason = Find("API_PRIVATE_PREVIEW_ACCESS_DENIED");
        if (reason is not null) return ("preview_access_denied", reason);
        reason = Find("MISSING_OAUTH_SCOPE") ?? Find("DISALLOWED_OAUTH_SCOPES");
        if (reason is not null) return ("permission_denied", reason);
        reason = Find("DATA_ACCESS_DENIED") ?? Find("RESOURCE_PERMISSION_DENIED");
        if (reason is not null) return ("data_access_denied", reason);
        reason = Find("RESOURCE_NOT_FOUND");
        if (reason is not null) return ("google_resource_not_found", reason);
        reason = Find("INVALID_TIME_RANGE");
        if (reason is not null) return ("invalid_time_range", reason);
        reason = Find("INVALID_DATA_POINT_FILTER_RESTRICTION_COMPARATOR");
        if (reason is not null) return ("invalid_filter_operator", reason);
        reason = FindPrefix("INVALID_DATA_POINT_FILTER") ?? FindPrefix("INVALID_FILTER");
        if (reason is not null) return ("invalid_google_filter", reason);
        reason = Find("INVALID_PARENT_DATA_TYPE_COLLECTION_FORMAT") ?? Find("INVALID_PARENT_DATA_TYPE_COLLECTION") ??
                 Find("INVALID_DATA_TYPE_FORMAT");
        if (reason is not null) return ("invalid_google_data_type", reason);
        reason = Find("INVALID_DATA_POINT_DATA_SOURCE_FAMILY");
        if (reason is not null) return ("invalid_source_family", reason);
        reason = Find("INTERNAL_ERROR");
        if (reason is not null) return ("google_unavailable", reason);
        reason = FindPrefix("INVALID_");
        return reason is null ? (fallback, null) : ("invalid_google_request", reason);
    }

    private static HashSet<string> GoogleReasons(JsonElement details)
    {
        var reasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var detail in details.EnumerateArray())
        {
            AddReasons(detail, "reason", reasons);
            if (!detail.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object) continue;
            foreach (var property in metadata.EnumerateObject())
                if (property.Name.Equals("detailedReasons", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("reason", StringComparison.OrdinalIgnoreCase))
                    AddReasons(property.Value, reasons);
        }
        return reasons;
    }

    private static void AddReasons(JsonElement source, string propertyName, HashSet<string> reasons)
    {
        if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty(propertyName, out var value))
            AddReasons(value, reasons);
    }

    private static void AddReasons(JsonElement value, HashSet<string> reasons)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            foreach (var reason in (value.GetString() ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = reason.Trim().Trim('[', ']', '"', '\'');
                if (normalized.Length > 0) reasons.Add(normalized);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var reason in value.EnumerateArray()) AddReasons(reason, reasons);
        }
    }

    public static string Key(GoogleHealthReading reading) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{reading.DataType}|{reading.Mills}|{reading.EndMills}")));

    public static SleepSession ParseSleep(JsonElement point)
    {
        try
        {
            var payload = point.GetProperty("sleep");
            var interval = payload.GetProperty("interval");
            var start = DateTimeOffset.Parse(interval.GetProperty("startTime").GetString()!, CultureInfo.InvariantCulture);
            var end = DateTimeOffset.Parse(interval.GetProperty("endTime").GetString()!, CultureInfo.InvariantCulture);
            if (end <= start) throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: "sleep");

            var stages = new List<SleepStageInterval>();
            if (payload.TryGetProperty("stages", out var stageData) && stageData.ValueKind == JsonValueKind.Array)
            {
                var ordinal = 0;
                foreach (var stage in stageData.EnumerateArray())
                {
                    var stageStart = DateTimeOffset.Parse(stage.GetProperty("startTime").GetString()!, CultureInfo.InvariantCulture);
                    var stageEnd = DateTimeOffset.Parse(stage.GetProperty("endTime").GetString()!, CultureInfo.InvariantCulture);
                    if (stageStart < start || stageEnd > end || stageEnd <= stageStart)
                        throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: "sleep");
                    stages.Add(new SleepStageInterval
                    {
                        StartTime = stageStart.UtcDateTime,
                        EndTime = stageEnd.UtcDateTime,
                        Stage = ParseSleepStage(stage.GetProperty("type").GetString()),
                        Ordinal = ordinal++
                    });
                }
            }

            JsonElement metadata = default;
            var hasMetadata = payload.TryGetProperty("metadata", out metadata) && metadata.ValueKind == JsonValueKind.Object;
            var isNap = hasMetadata && metadata.TryGetProperty("nap", out var nap) && nap.ValueKind == JsonValueKind.True;
            var manuallyEdited = hasMetadata && metadata.TryGetProperty("manuallyEdited", out var manual) && manual.ValueKind == JsonValueKind.True;
            var processed = hasMetadata && metadata.TryGetProperty("processed", out var complete) && complete.ValueKind == JsonValueKind.True;

            JsonElement summary = default;
            var hasSummary = payload.TryGetProperty("summary", out summary) && summary.ValueKind == JsonValueKind.Object;
            var durationMs = (long)(end - start).TotalMilliseconds;
            var stagedSleepMs = StageMilliseconds(stages, SleepStageType.Asleep, SleepStageType.Light, SleepStageType.Deep, SleepStageType.Rem);
            var sleepMinutes = hasSummary ? Minutes(summary, "minutesAsleep") : null;
            var totalSleepMs = sleepMinutes.HasValue ? sleepMinutes.Value * 60_000 : stagedSleepMs;
            if (totalSleepMs == 0 && stages.Count == 0) totalSleepMs = durationMs;
            var stagedAwakeMs = StageMilliseconds(stages, SleepStageType.Awake, SleepStageType.AwakeInBed);
            var awakeMinutes = hasSummary ? Minutes(summary, "minutesAwake") : null;
            long? totalAwakeMs = awakeMinutes.HasValue
                ? awakeMinutes.Value * 60_000
                : stages.Count > 0 ? stagedAwakeMs : null;
            var latencyMinutes = hasSummary ? Minutes(summary, "minutesToFallAsleep") : null;

            var externalId = hasMetadata && metadata.TryGetProperty("externalId", out var external) && external.ValueKind == JsonValueKind.String
                ? external.GetString()
                : null;
            var resourceName = point.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            var originalId = !string.IsNullOrWhiteSpace(resourceName) ? resourceName : externalId;
            originalId ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"sleep|{start:O}|{end:O}")));

            return new SleepSession
            {
                StartTime = start.UtcDateTime,
                EndTime = end.UtcDateTime,
                Type = isNap ? SleepSessionType.Nap : SleepSessionType.Overnight,
                DetectionMethod = manuallyEdited ? SleepDetectionMethod.Manual : processed ? SleepDetectionMethod.AutoFinal : SleepDetectionMethod.AutoTentative,
                IsMainSleep = !isNap,
                DurationMs = durationMs,
                TotalSleepMs = totalSleepMs,
                TotalAwakeMs = totalAwakeMs,
                DeepSleepMs = stages.Count > 0 ? StageMilliseconds(stages, SleepStageType.Deep) : null,
                LightSleepMs = stages.Count > 0 ? StageMilliseconds(stages, SleepStageType.Light) : null,
                RemSleepMs = stages.Count > 0 ? StageMilliseconds(stages, SleepStageType.Rem) : null,
                SleepLatencyMs = latencyMinutes.HasValue
                    ? latencyMinutes.Value * 60_000
                    : null,
                Efficiency = durationMs > 0 ? (float)(totalSleepMs * 100d / durationMs) : null,
                RestlessPeriods = stages.Count > 0 ? stages.Count(stage => stage.Stage == SleepStageType.Restless) : null,
                Source = SleepSource.Google,
                SourceApp = "Google Health",
                OriginalId = originalId,
                Stages = stages,
                Metadata = new Dictionary<string, object>
                {
                    ["googleSleepType"] = payload.TryGetProperty("type", out var sleepType) ? sleepType.GetString() ?? "SLEEP_TYPE_UNSPECIFIED" : "SLEEP_TYPE_UNSPECIFIED",
                    ["manuallyEdited"] = manuallyEdited
                }
            };
        }
        catch (GoogleHealthException) { throw; }
        catch (Exception ex) when (ex is KeyNotFoundException or FormatException or InvalidOperationException or OverflowException or ArgumentException)
        {
            throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: "sleep");
        }
    }

    private static SleepStageType ParseSleepStage(string? value) => value switch
    {
        "AWAKE" => SleepStageType.Awake,
        "LIGHT" => SleepStageType.Light,
        "DEEP" => SleepStageType.Deep,
        "REM" => SleepStageType.Rem,
        "ASLEEP" => SleepStageType.Asleep,
        "RESTLESS" => SleepStageType.Restless,
        _ => SleepStageType.Unknown
    };

    private static long StageMilliseconds(IEnumerable<SleepStageInterval> stages, params SleepStageType[] types) =>
        stages.Where(stage => types.Contains(stage.Stage)).Sum(stage => (long)(stage.EndTime - stage.StartTime).TotalMilliseconds);

    private static long? Minutes(JsonElement summary, string property) =>
        summary.TryGetProperty(property, out var value) && long.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var minutes) && minutes >= 0
            ? minutes
            : null;

    public static GoogleHealthReading Parse(string type, JsonElement point)
    {
        try
        {
            var payload = point.GetProperty(type == "heart-rate" ? "heartRate" : type);
            var interval = type == "steps";
            var time = payload.GetProperty(interval ? "interval" : "sampleTime");
            var start = DateTimeOffset.Parse(time.GetProperty(interval ? "startTime" : "physicalTime").GetString()!, CultureInfo.InvariantCulture);
            long? end = interval ? DateTimeOffset.Parse(time.GetProperty("endTime").GetString()!, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds() : null;
            var valueName = type switch { "steps" => "count", "heart-rate" => "beatsPerMinute", "weight" => "weightGrams", _ => throw new GoogleHealthException("unsupported_type", stage: "data_parse", dataType: type) };
            var value = decimal.Parse(payload.GetProperty(valueName).ToString(), CultureInfo.InvariantCulture);
            if (value < 0 || (type != "steps" && value == 0) || (type != "weight" && decimal.Truncate(value) != value) || (end.HasValue && end.Value <= start.ToUnixTimeMilliseconds()))
                throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: type);
            int? offset = null;
            if (time.TryGetProperty(interval ? "startUtcOffset" : "utcOffset", out var offsetValue))
            {
                var seconds = decimal.Parse(offsetValue.GetString()!.TrimEnd('s'), CultureInfo.InvariantCulture);
                if (seconds % 60 != 0 || Math.Abs(seconds) > 50400) throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: type);
                offset = (int)(seconds / 60);
            }
            return new GoogleHealthReading
            {
                DataType = type, Mills = start.ToUnixTimeMilliseconds(), EndMills = end, UtcOffsetMinutes = offset,
                Value = type == "weight" ? value / 1000m : value,
                Unit = type switch { "weight" => "kg", "heart-rate" => "bpm", _ => "steps" }
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or FormatException or InvalidOperationException or OverflowException or ArgumentException)
        {
            throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: type);
        }
    }
}
