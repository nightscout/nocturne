using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Resolves the active profile name and CircadianPercentageProfile adjustments at a given time
/// by querying Profile <see cref="StateSpan"/> records via <see cref="IStateSpanService"/>.
/// Results are cached per tenant with a 5-second TTL, keyed by minute-rounded time.
/// </summary>
internal sealed class ActiveProfileResolver : IActiveProfileResolver
{
    private readonly IStateSpanService _stateSpanService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ActiveProfileResolver> _logger;

    private const int CacheTtlSeconds = 5;
    private const long MillisPerMinute = 60_000;

    public ActiveProfileResolver(
        IStateSpanService stateSpanService,
        ITenantAccessor tenantAccessor,
        IMemoryCache cache,
        ILogger<ActiveProfileResolver> logger)
    {
        _stateSpanService = stateSpanService;
        _tenantAccessor = tenantAccessor;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string?> GetActiveProfileNameAsync(long timeMills, CancellationToken ct = default)
    {
        var span = await GetActiveProfileSpanAsync(timeMills, ct);
        return ExtractProfileName(span);
    }

    public async Task<CircadianAdjustment?> GetCircadianAdjustmentAsync(long timeMills, CancellationToken ct = default)
    {
        var span = await GetActiveProfileSpanAsync(timeMills, ct);
        return ExtractCircadianAdjustment(span);
    }

    public async Task<IReadOnlyList<ProfileSpan>> GetActiveProfileSpansForRangeAsync(
        long fromMs, long toMs, CancellationToken ct = default)
    {
        var toDateTime = DateTimeOffset.FromUnixTimeMilliseconds(toMs).UtcDateTime;

        // Fetch all profile spans that started at or before range end.
        // Includes spans that started before [fromMs] but are still active during the range.
        // No `from:` filter for this reason — same pattern as TherapyTimelineResolver.
        var rawSpans = await _stateSpanService.GetStateSpansAsync(
            category: StateSpanCategory.Profile,
            to: toDateTime,
            count: 1000,
            cancellationToken: ct);

        // Sort in-memory for reliable chronological order regardless of DB ordering.
        return rawSpans
            .Select(s => new ProfileSpan(
                ProfileName: ExtractProfileName(s) ?? "Default",
                StartMills: s.StartMills,
                EndMills: s.EndMills,
                Adjustment: ExtractCircadianAdjustment(s)))
            .OrderBy(s => s.StartMills)
            .ToList();
    }

    public async Task<TreatmentInsulinContext?> GetActiveInsulinContextAsync(long timeMills, CancellationToken ct = default)
    {
        var span = await GetActiveProfileSpanAsync(timeMills, ct);
        return ExtractInsulinContext(span);
    }

    private async Task<StateSpan?> GetActiveProfileSpanAsync(long timeMills, CancellationToken ct)
    {
        var minuteRounded = timeMills / MillisPerMinute * MillisPerMinute;
        var tenantId = _tenantAccessor.TenantId;
        var cacheKey = $"ActiveProfile:{tenantId}:{minuteRounded}";

        if (_cache.TryGetValue(cacheKey, out StateSpan? cached))
            return cached;

        var queryTime = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;

        // Query profile StateSpans that could cover the requested time.
        // We query with to=queryTime so we get spans that started at or before the time.
        var spans = await _stateSpanService.GetStateSpansAsync(
            category: StateSpanCategory.Profile,
            to: queryTime,
            count: 100,
            cancellationToken: ct);

        // Find the span that covers the requested time:
        // StartMills <= timeMills AND (EndMills is null OR EndMills > timeMills)
        var activeSpan = spans
            .Where(s => s.StartMills <= timeMills && (!s.EndMills.HasValue || s.EndMills.Value > timeMills))
            .OrderByDescending(s => s.StartMills)
            .FirstOrDefault();

        _cache.Set(cacheKey, activeSpan, TimeSpan.FromSeconds(CacheTtlSeconds));

        return activeSpan;
    }

    private static string? ExtractProfileName(StateSpan? span)
    {
        if (span?.Metadata is null)
            return null;

        if (span.Metadata.TryGetValue("profileName", out var value))
            return ConvertToString(value);

        return null;
    }

    private static CircadianAdjustment? ExtractCircadianAdjustment(StateSpan? span)
    {
        if (span?.Metadata is null)
            return null;

        if (!span.Metadata.TryGetValue("percentage", out var pctValue))
            return null;

        var percentage = ConvertToDouble(pctValue);
        if (percentage is null)
            return null;

        long timeshiftMs = 0;
        if (span.Metadata.TryGetValue("timeshift", out var tsValue))
        {
            var timeshiftHours = ConvertToDouble(tsValue) ?? 0;
            timeshiftMs = (long)(timeshiftHours % 24 * 3_600_000);
        }

        return new CircadianAdjustment(percentage.Value, timeshiftMs);
    }

    private static TreatmentInsulinContext? ExtractInsulinContext(StateSpan? span)
    {
        if (span?.Metadata is null)
            return null;

        if (!span.Metadata.TryGetValue("insulinDia", out var diaValue))
            return null;

        var dia = ConvertToDouble(diaValue);
        if (dia is null or <= 0)
            return null;

        var peak = span.Metadata.TryGetValue("insulinPeak", out var peakValue)
            ? (int)(ConvertToDouble(peakValue) ?? 0)
            : 0;

        if (peak <= 0)
            return null;

        var name = span.Metadata.TryGetValue("insulinName", out var nameValue)
            ? ConvertToString(nameValue) ?? ""
            : "";

        var concentration = span.Metadata.TryGetValue("insulinConcentration", out var concValue)
            ? (int)(ConvertToDouble(concValue) ?? 100)
            : 100;

        var curve = span.Metadata.TryGetValue("insulinCurve", out var curveValue)
            ? ConvertToString(curveValue) ?? "rapid-acting"
            : "rapid-acting";

        return new TreatmentInsulinContext
        {
            PatientInsulinId = Guid.Empty,
            InsulinName = name,
            Dia = dia.Value,
            Peak = peak,
            Concentration = concentration,
            Curve = curve,
        };
    }

    /// <summary>
    /// Converts a metadata value (which may be a <see cref="JsonElement"/> after deserialization)
    /// to a string.
    /// </summary>
    private static string? ConvertToString(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => value.ToString(),
    };

    /// <summary>
    /// Converts a metadata value (which may be a <see cref="JsonElement"/> after deserialization)
    /// to a double.
    /// </summary>
    private static double? ConvertToDouble(object? value) => value switch
    {
        null => null,
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
        _ => double.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null,
    };
}
