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
/// Candidate spans are cached per tenant with a 5-second TTL, keyed by minute.
/// </summary>
internal sealed class ActiveProfileResolver : IActiveProfileResolver
{
    private readonly IStateSpanService _stateSpanService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IMemoryCache _cache;
    private readonly ICategoryReadContext _categoryReadContext;
    private readonly ILogger<ActiveProfileResolver> _logger;

    private const int CacheTtlSeconds = 5;
    private const long MillisPerMinute = 60_000;

    public ActiveProfileResolver(
        IStateSpanService stateSpanService,
        ITenantAccessor tenantAccessor,
        IMemoryCache cache,
        ICategoryReadContext categoryReadContext,
        ILogger<ActiveProfileResolver> logger)
    {
        _stateSpanService = stateSpanService;
        _tenantAccessor = tenantAccessor;
        _cache = cache;
        _categoryReadContext = categoryReadContext;
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
        var fromDateTime = DateTimeOffset.FromUnixTimeMilliseconds(fromMs).UtcDateTime;
        var toDateTime = DateTimeOffset.FromUnixTimeMilliseconds(toMs).UtcDateTime;

        // from: is an overlap bound, so spans that started before the range but are still running stay in.
        var rawSpans = await _stateSpanService.GetStateSpansAsync(
            category: StateSpanCategory.Profile,
            from: fromDateTime,
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

    /// <remarks>
    /// The cache holds the newest spans that overlap the minute, so the active span is picked at
    /// the exact time and the answer does not depend on which lookup in the minute came first. A request
    /// that <see cref="ICategoryReadContext.BypassesTenantCaches"/> neither reads nor writes it.
    /// </remarks>
    private async Task<StateSpan?> GetActiveProfileSpanAsync(long timeMills, CancellationToken ct)
    {
        var minuteStart = timeMills / MillisPerMinute * MillisPerMinute;
        var tenantId = _tenantAccessor.TenantId;
        var cacheKey = $"ActiveProfile:{tenantId}:{minuteStart}";
        var useCache = !_categoryReadContext.BypassesTenantCaches;

        if (!useCache || !_cache.TryGetValue(cacheKey, out IReadOnlyList<StateSpan>? candidates) || candidates is null)
        {
            var minuteFrom = DateTimeOffset.FromUnixTimeMilliseconds(minuteStart).UtcDateTime;
            var minuteEnd = DateTimeOffset.FromUnixTimeMilliseconds(minuteStart + MillisPerMinute - 1).UtcDateTime;
            candidates = (await _stateSpanService.GetStateSpansAsync(
                category: StateSpanCategory.Profile,
                from: minuteFrom,
                to: minuteEnd,
                count: 100,
                cancellationToken: ct)).ToList();

            if (useCache)
                _cache.Set(cacheKey, candidates, TimeSpan.FromSeconds(CacheTtlSeconds));
        }

        return candidates
            .Where(s => s.StartMills <= timeMills && (!s.EndMills.HasValue || s.EndMills.Value > timeMills))
            .OrderByDescending(s => s.StartMills)
            .FirstOrDefault();
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
