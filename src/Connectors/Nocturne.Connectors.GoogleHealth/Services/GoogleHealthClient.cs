using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Health;

namespace Nocturne.Connectors.GoogleHealth.Services;

public sealed class GoogleHealthException(
    string code,
    TimeSpan? retryAfter = null,
    string? stage = null,
    string? dataType = null,
    string? providerReason = null,
    int? providerStatus = null,
    Exception? innerException = null) : Exception(code, innerException)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public string? Stage { get; } = stage;
    public string? DataType { get; } = dataType;
    public string? ProviderReason { get; } = providerReason;
    public int? ProviderStatus { get; } = providerStatus;

    /// <summary>The stored session can never work again; only a new sign-in recovers.</summary>
    public bool RequiresReconnect => Message is "reconnect_required" or "client_changed";
}

public sealed class GoogleHealthClient(HttpClient http, ILogger<GoogleHealthClient>? logger = null)
{
    // A faulty provider can keep issuing unique cursors that bypass cycle detection.
    internal const int MaximumHistoryPages = 10_000;

    public const string ActivityScope = "https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly";
    public const string MetricsScope = "https://www.googleapis.com/auth/googlehealth.health_metrics_and_measurements.readonly";
    public const string NutritionScope = "https://www.googleapis.com/auth/googlehealth.nutrition.readonly";
    public const string SleepScope = "https://www.googleapis.com/auth/googlehealth.sleep.readonly";
    public static readonly GoogleHealthCapability[] Capabilities =
    [
        new() { DataType = "steps", Category = GoogleHealthCategoryKind.Activity, Supported = true, Destination = "step-counts" },
        new() { DataType = "heart-rate", Category = GoogleHealthCategoryKind.Vitals, Supported = true, Destination = "heart-rates" },
        new() { DataType = "weight", Category = GoogleHealthCategoryKind.BodyMeasurement, Supported = true, Destination = "body-weights" },
        new() { DataType = "sleep", Category = GoogleHealthCategoryKind.Sleep, Supported = true, Destination = "sleep-sessions" },
        new() { DataType = "active-energy-burned", Category = GoogleHealthCategoryKind.Activity },
        new() { DataType = "total-calories", Category = GoogleHealthCategoryKind.Activity },
        new() { DataType = "distance", Category = GoogleHealthCategoryKind.Activity },
        new() { DataType = "floors", Category = GoogleHealthCategoryKind.Activity },
        new() { DataType = "exercise", Category = GoogleHealthCategoryKind.Activity },
        new() { DataType = "body-fat", Category = GoogleHealthCategoryKind.BodyMeasurement },
        new() { DataType = "height", Category = GoogleHealthCategoryKind.BodyMeasurement },
        new() { DataType = "nutrition-log", Category = GoogleHealthCategoryKind.Nutrition },
        new() { DataType = "hydration-log", Category = GoogleHealthCategoryKind.Nutrition },
        new() { DataType = "blood-glucose", Category = GoogleHealthCategoryKind.Vitals },
        new() { DataType = "oxygen-saturation", Category = GoogleHealthCategoryKind.Vitals },
        new() { DataType = "heart-rate-variability", Category = GoogleHealthCategoryKind.Vitals },
        new() { DataType = "daily-resting-heart-rate", Category = GoogleHealthCategoryKind.Vitals },
        new() { DataType = "daily-oxygen-saturation", Category = GoogleHealthCategoryKind.Vitals },
        new() { DataType = "daily-respiratory-rate", Category = GoogleHealthCategoryKind.Vitals },
        new() { DataType = "respiratory-rate-sleep-summary", Category = GoogleHealthCategoryKind.Vitals },
        new() { DataType = "core-body-temperature", Category = GoogleHealthCategoryKind.Vitals },
        new() { DataType = "menstrual-period", Category = GoogleHealthCategoryKind.CycleTracking, UnavailableReason = GoogleHealthTypeState.NoReadonlyScope },
        new() { DataType = "ovulation-test", Category = GoogleHealthCategoryKind.CycleTracking, UnavailableReason = GoogleHealthTypeState.NoReadonlyScope },
        new() { DataType = "cervical-mucus", Category = GoogleHealthCategoryKind.CycleTracking, UnavailableReason = GoogleHealthTypeState.NotReadable },
        new() { DataType = "sexual-activity", Category = GoogleHealthCategoryKind.CycleTracking, UnavailableReason = GoogleHealthTypeState.NotReadable }
    ];
    public static string[] SupportedTypes => Capabilities.Where(c => c.Supported).Select(c => c.DataType).ToArray();
    public static bool TryGetDataType(SyncDataType type, out string dataType) =>
        SyncDataTypes.TryGetValue(type, out dataType!);

    public static bool TryGetSyncDataType(string dataType, out SyncDataType type) =>
        DataTypesByName.TryGetValue(dataType, out type);

    private static readonly IReadOnlyDictionary<SyncDataType, string> SyncDataTypes =
        new Dictionary<SyncDataType, string>
        {
            [SyncDataType.Steps] = "steps",
            [SyncDataType.HeartRate] = "heart-rate",
            [SyncDataType.BodyWeight] = "weight",
            [SyncDataType.Sleep] = "sleep"
        };

    private static readonly IReadOnlyDictionary<string, SyncDataType> DataTypesByName =
        SyncDataTypes.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    public static string ScopeFor(string type) => type switch
    {
        "steps" => ActivityScope,
        "heart-rate" or "weight" or "body-fat" or "height" or "blood-glucose" or "oxygen-saturation" or
            "heart-rate-variability" or "daily-resting-heart-rate" or "daily-oxygen-saturation" or
            "daily-respiratory-rate" or "respiratory-rate-sleep-summary" or "core-body-temperature" => MetricsScope,
        "distance" or "active-energy-burned" or "total-calories" or "floors" or "exercise" => ActivityScope,
        "nutrition-log" or "hydration-log" => NutritionScope,
        "sleep" => SleepScope,
        _ => throw new GoogleHealthException("unsupported_type")
    };

    private static string FormatFilterTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string TimeField(string type) => type switch
    {
        "steps" or "distance" => $"{type.Replace('-', '_')}.interval.start_time",
        "sleep" => "sleep.interval.end_time",
        _ => $"{type.Replace('-', '_')}.sample_time.physical_time"
    };

    public async Task<int> CountAsync(string token, string type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var count = 0;
        await foreach (var (_, points) in PagesAsync(token, type, from, to, "inventory", ct))
            count += points.Length;
        return count;
    }

    public async IAsyncEnumerable<IReadOnlyCollection<SleepSession>> ReadSleepPagesAsync(
        string token, DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken ct,
        Func<int, Task>? onPageRead = null)
    {
        await foreach (var (page, points) in PagesAsync(token, "sleep", from, to, "data_read", ct))
        {
            var sessions = points.Select(ParseSleep)
                .Where(session => session.EndMills >= from.ToUnixTimeMilliseconds() &&
                                  session.EndMills < to.ToUnixTimeMilliseconds())
                .ToArray();
            if (onPageRead is not null) await onPageRead(page);
            yield return sessions;
        }
    }

    public async IAsyncEnumerable<IReadOnlyCollection<GoogleHealthReading>> ReadPagesAsync(
        string token, string type, DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken ct,
        Func<int, Task>? onPageRead = null)
    {
        await foreach (var (page, points) in PagesAsync(token, type, from, to, "data_read", ct))
        {
            var readings = points.Select(point => Parse(type, point))
                .Where(reading => reading.Mills >= from.ToUnixTimeMilliseconds() &&
                                  reading.Mills < to.ToUnixTimeMilliseconds())
                .ToArray();
            if (onPageRead is not null) await onPageRead(page);
            yield return readings;
        }
    }

    /// <summary>
    /// Every page of <paramref name="type"/>'s data points in the window, numbered from one. Stops
    /// at a repeated page token or <see cref="MaximumHistoryPages"/> rather than trusting Google
    /// to end the listing.
    /// </summary>
    private async IAsyncEnumerable<(int Page, JsonElement[] Points)> PagesAsync(
        string token, string type, DateTimeOffset from, DateTimeOffset to, string stage,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var field = TimeField(type);
        var filter = $"{field} >= \"{FormatFilterTime(from)}\" AND {field} < \"{FormatFilterTime(to)}\"";
        var pageSize = type == "sleep" ? 25 : 10000;
        var root = $"https://health.googleapis.com/v4/users/me/dataTypes/{type}/dataPoints:reconcile?pageSize={pageSize}&filter={Uri.EscapeDataString(filter)}";
        var seen = new HashSet<string>();
        var pageToken = "";
        for (var page = 1; page <= MaximumHistoryPages; page++)
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
            JsonElement[] points;
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                points = json.RootElement.TryGetProperty("dataPoints", out var data)
                    ? data.EnumerateArray().Select(point => point.Clone()).ToArray()
                    : [];
                pageToken = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() ?? "" : "";
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new GoogleHealthException("invalid_google_response", stage: stage, dataType: type);
            }
            yield return (page, points);
            if (pageToken.Length == 0) yield break;
            if (!seen.Add(pageToken)) throw new GoogleHealthException("pagination_failed", stage: stage, dataType: type);
        }
        throw new GoogleHealthException("history_too_large", stage: stage, dataType: type);
    }

    private async Task<GoogleHealthException> ErrorAsync(
        HttpResponseMessage response, string fallback, string dataType, CancellationToken ct)
    {
        var code = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "rate_limited" : fallback;
        string? providerReason = null;
        string? responseBody = null;
        try
        {
            responseBody = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                using var json = JsonDocument.Parse(responseBody);
                if (json.RootElement.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    var reasons = GoogleReasons(details);
                    (code, providerReason) = MapGoogleReason(reasons, code);
                    providerReason = GoogleHealthHttpError.SafeProviderReason(providerReason);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            logger?.LogWarning(ex, "Failed to parse Google API error response body for data type {DataType}", dataType);
        }
        logger?.LogWarning("Google Health API error HTTP {StatusCode} for {DataType}: {Code}, {ProviderReason}",
            (int)response.StatusCode, dataType, code, providerReason);
        return new GoogleHealthException(code, GoogleHealthHttpError.RetryAfter(response), "data_read", dataType, providerReason,
            (int)response.StatusCode);
    }

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
        Encoding.UTF8.GetBytes($"{reading.DataType}|{reading.OriginalId ?? $"{reading.Mills}|{reading.EndMills}"}")));

    private static string? ResourceName(JsonElement point)
    {
        foreach (var property in new[] { "dataPointName", "name" })
            if (point.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        return null;
    }

    public static SleepSession ParseSleep(JsonElement point)
    {
        try
        {
            var payload = point.GetProperty("sleep");
            var interval = payload.GetProperty("interval");
            var start = DateTimeOffset.Parse(interval.GetProperty("startTime").GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var end = DateTimeOffset.Parse(interval.GetProperty("endTime").GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            if (end <= start) throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: "sleep");

            var stages = new List<SleepStageInterval>();
            if (payload.TryGetProperty("stages", out var stageData))
            {
                var ordinal = 0;
                foreach (var stage in stageData.EnumerateArray())
                {
                    var stageStart = DateTimeOffset.Parse(stage.GetProperty("startTime").GetString()!, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                    var stageEnd = DateTimeOffset.Parse(stage.GetProperty("endTime").GetString()!, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
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

            var orderedStages = stages.OrderBy(stage => stage.StartTime).ToArray();
            for (var index = 1; index < orderedStages.Length; index++)
                if (orderedStages[index].StartTime < orderedStages[index - 1].EndTime)
                    throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: "sleep");

            JsonElement metadata = default;
            var hasMetadata = payload.TryGetProperty("metadata", out metadata) && metadata.ValueKind == JsonValueKind.Object;
            var isNap = hasMetadata && metadata.TryGetProperty("nap", out var nap) && nap.ValueKind == JsonValueKind.True;
            var manuallyEdited = hasMetadata && metadata.TryGetProperty("manuallyEdited", out var manual) && manual.ValueKind == JsonValueKind.True;
            var processed = hasMetadata && metadata.TryGetProperty("processed", out var complete) && complete.ValueKind == JsonValueKind.True;

            JsonElement summary = default;
            var hasSummary = payload.TryGetProperty("summary", out summary);
            if (hasSummary && summary.ValueKind != JsonValueKind.Object)
                throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: "sleep");
            var durationMs = (long)(end - start).TotalMilliseconds;
            var stagedSleepMs = StageMilliseconds(stages, SleepStageType.Asleep, SleepStageType.Light, SleepStageType.Deep, SleepStageType.Rem);
            var sleepMs = hasSummary ? SummaryMilliseconds(summary, "minutesAsleep", durationMs) : null;
            var totalSleepMs = sleepMs ?? stagedSleepMs;
            var stagedAwakeMs = StageMilliseconds(stages, SleepStageType.Awake, SleepStageType.AwakeInBed);
            var awakeMs = hasSummary ? SummaryMilliseconds(summary, "minutesAwake", durationMs) : null;
            if (!sleepMs.HasValue && stages.Count == 0) totalSleepMs = durationMs - (awakeMs ?? 0);
            long? totalAwakeMs = awakeMs ?? (stages.Count > 0 ? stagedAwakeMs : null);
            var latencyMs = hasSummary ? SummaryMilliseconds(summary, "minutesToFallAsleep", durationMs) : null;
            if (stagedSleepMs > durationMs || stagedAwakeMs > durationMs ||
                stagedSleepMs + stagedAwakeMs > durationMs ||
                totalSleepMs > durationMs || totalSleepMs + (totalAwakeMs ?? 0) > durationMs)
                throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: "sleep");

            var externalId = hasMetadata && metadata.TryGetProperty("externalId", out var external) && external.ValueKind == JsonValueKind.String
                ? external.GetString()
                : null;
            var resourceName = ResourceName(point);
            var originalId = !string.IsNullOrWhiteSpace(resourceName) ? resourceName :
                !string.IsNullOrWhiteSpace(externalId) ? externalId : null;
            originalId ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"sleep|{start:O}|{end:O}")));
            // SleepSource.Google is shared; its upsert key must distinguish this connector.
            originalId = "googlehealth:sleep:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(originalId)));

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
                SleepLatencyMs = latencyMs,
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

    private static long? SummaryMilliseconds(JsonElement summary, string property, long durationMs)
    {
        if (!summary.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
            !long.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var minutes) ||
            minutes < 0 || minutes > durationMs / 60_000)
            throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: "sleep");
        return checked(minutes * 60_000);
    }

    public static GoogleHealthReading Parse(string type, JsonElement point)
    {
        try
        {
            var payload = point.GetProperty(type == "heart-rate" ? "heartRate" : type);
            var interval = type == "steps";
            var time = payload.GetProperty(interval ? "interval" : "sampleTime");
            var start = DateTimeOffset.Parse(time.GetProperty(interval ? "startTime" : "physicalTime").GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            long? end = interval ? DateTimeOffset.Parse(time.GetProperty("endTime").GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).ToUnixTimeMilliseconds() : null;
            var valueName = type switch { "steps" => "count", "heart-rate" => "beatsPerMinute", "weight" => "weightGrams", _ => throw new GoogleHealthException("unsupported_type", stage: "data_parse", dataType: type) };
            var value = decimal.Parse(payload.GetProperty(valueName).ToString(), CultureInfo.InvariantCulture);
            if (value < 0 || (type != "steps" && value == 0) ||
                (type != "weight" && (decimal.Truncate(value) != value || value > int.MaxValue)) ||
                (end.HasValue && end.Value <= start.ToUnixTimeMilliseconds()))
                throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: type);
            int? offset = null;
            if (time.TryGetProperty(interval ? "startUtcOffset" : "utcOffset", out var offsetValue))
            {
                if (offsetValue.ValueKind != JsonValueKind.String || offsetValue.GetString() is not { } duration)
                    throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: type);
                if (duration.Length < 2 || duration[^1] != 's' ||
                    !decimal.TryParse(duration.AsSpan(0, duration.Length - 1),
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var seconds) ||
                    seconds % 60 != 0 || Math.Abs(seconds) > 50400)
                    throw new GoogleHealthException("invalid_google_data", stage: "data_parse", dataType: type);
                offset = (int)(seconds / 60);
            }
            return new GoogleHealthReading
            {
                DataType = type,
                OriginalId = ResourceName(point),
                Mills = start.ToUnixTimeMilliseconds(), EndMills = end, UtcOffsetMinutes = offset,
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
