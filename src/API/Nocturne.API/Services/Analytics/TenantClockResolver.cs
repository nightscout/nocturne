using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Analytics;

/// <summary>The tenant's timezone, or no zone and why not.</summary>
public readonly record struct TenantClock(TimeZoneInfo? Zone, TimeZoneUnavailableReason? Reason)
{
    /// <remarks>
    /// <paramref name="calendarDays"/> is the days of the window on the report's calendar, which
    /// <see cref="DataSufficiencyAssessment.DaysWithData"/> counts readings on. The per-day figures
    /// keep the window's duration.
    /// </remarks>
    public T Annotate<T>(T analytics, int? calendarDays = null) where T : GlucoseAnalytics
    {
        analytics.TimeZoneUnavailableReason = Reason;
        if (analytics.GlycemicVariability is { } variability)
            variability.TimeZoneUnavailableReason = Reason;
        if (analytics is ExtendedGlucoseAnalytics extended)
        {
            extended.DataSufficiency.TimeZoneUnavailableReason = Reason;
            if (calendarDays is { } days)
                extended.DataSufficiency.TotalDays = days;
        }
        return analytics;
    }
}

/// <summary>
/// Resolves the tenant's timezone for the reports that place readings on its local clock: none is
/// set, the stored one does not resolve, or the lookup threw.
/// </summary>
/// <remarks>A share reads the zone id through <see cref="IShareClockReader"/>.</remarks>
public sealed class TenantClockResolver(
    ITherapySettingsResolver therapySettingsResolver,
    ICategoryReadContext categoryReadContext,
    IShareClockReader shareClockReader,
    ILogger<TenantClockResolver> logger)
{
    public async Task<TenantClock> ResolveAsync(CancellationToken ct)
    {
        try
        {
            var id = categoryReadContext.IsShare
                ? await shareClockReader.ReadTimezoneAsync(ct)
                : await therapySettingsResolver.GetTimezoneAsync(ct: ct);
            if (TimeZoneHelper.TryGetTimeZoneInfoFromId(id, out var tz))
                return new TenantClock(tz, null);

            return new TenantClock(
                null,
                string.IsNullOrWhiteSpace(id)
                    ? TimeZoneUnavailableReason.NotConfigured
                    : TimeZoneUnavailableReason.Unrecognised);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Tenant timezone lookup failed; reports go without the tenant's zone");
            return new TenantClock(null, TimeZoneUnavailableReason.LookupFailed);
        }
    }
}
