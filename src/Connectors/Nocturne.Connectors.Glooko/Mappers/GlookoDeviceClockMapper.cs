using System.Globalization;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.Timezones;

namespace Nocturne.Connectors.Glooko.Mappers;

/// <summary>
/// Maps clock-probe payloads to <see cref="DeviceClockObservation"/>s. Pure record-to-evidence
/// mapping; the estimator math lives in <see cref="DeviceClockEstimator"/>.
/// </summary>
public static class GlookoDeviceClockMapper
{
    /// <summary>
    /// Maps the account's SSV2 user record to a profile observation, or null when the record asserts
    /// nothing usable:
    /// <list type="bullet">
    /// <item>Never-set: a zero offset with no declared zone is the server's placeholder, not a claim
    /// that the device runs on UTC — seeding from it would shift the tenant's entire history.</item>
    /// <item>Staleness needs no cutoff: the observation is recorded at the record's own
    /// <c>updatedAt</c>, so an old assertion enters the evidence at the moment it was made rather
    /// than being mistaken for the current state.</item>
    /// </list>
    /// </summary>
    public static DeviceClockObservation? MapProfileObservation(GlookoSsv2User[]? users, string connector)
    {
        var user = users?.FirstOrDefault(u => !u.SoftDeleted);
        if (user is null)
            return null;

        var offsetMinutes = ParseOffsetMinutes(user.UtcOffset);
        var updatedAt = ParseTimestamp(user.UpdatedAt);
        if (offsetMinutes is null || updatedAt is null)
            return null;

        if (Math.Abs(offsetMinutes.Value) > DeviceClockEstimator.MaxPlausibleOffsetMinutes)
            return null;

        if (offsetMinutes == 0 && string.IsNullOrWhiteSpace(user.Timezone))
            return null;

        return new DeviceClockObservation
        {
            Connector = connector,
            Source = DeviceClockObservationSource.Profile,
            ObservedAtUtc = updatedAt.Value,
            OffsetMinutes = offsetMinutes.Value,
            IsEstimate = true,
            SampleCount = 1,
            DeclaredTimezone = string.IsNullOrWhiteSpace(user.Timezone) ? null : user.Timezone,
        };
    }

    /// <summary>
    /// Maps recent CGM and bolus records to upload-batch observations. The feeds are estimated
    /// separately: CGM density is what earns a batch its two-sided estimate, and mixing sparse bolus
    /// records into a CGM batch would dilute the spacing that proves promptness.
    /// </summary>
    public static IReadOnlyList<DeviceClockObservation> MapUploadBatches(
        string connector, GlookoClockEgv[]? egvs, GlookoClockBolus[]? boluses)
    {
        var observations = new List<DeviceClockObservation>();

        observations.AddRange(DeviceClockEstimator.FromUploadBatches(
            connector,
            (egvs ?? [])
                .Where(e => e is { Calculated: false, SoftDeleted: false })
                .Select(e => (Clinical: ParseTimestamp(e.DisplayTime), Sync: ParseTimestamp(e.SyncTimestamp)))
                .Where(p => p is { Clinical: not null, Sync: not null })
                .Select(p => (p.Clinical!.Value, p.Sync!.Value))));

        observations.AddRange(DeviceClockEstimator.FromUploadBatches(
            connector,
            (boluses ?? [])
                .Where(b => !b.SoftDeleted)
                .Select(b => (
                    Clinical: ParseTimestamp(string.IsNullOrWhiteSpace(b.PumpTimestamp) ? b.Timestamp : b.PumpTimestamp),
                    Sync: ParseTimestamp(b.SyncTimestamp)))
                .Where(p => p is { Clinical: not null, Sync: not null })
                .Select(p => (p.Clinical!.Value, p.Sync!.Value))));

        return observations;
    }

    /// <summary>Parses Glooko's <c>+HH:MM</c>/<c>-HH:MM</c> offset notation to minutes east of UTC.</summary>
    public static int? ParseOffsetMinutes(string? utcOffset)
    {
        if (string.IsNullOrWhiteSpace(utcOffset))
            return null;

        var negative = utcOffset[0] == '-';
        var body = utcOffset[0] is '+' or '-' ? utcOffset[1..] : utcOffset;
        var parts = body.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            return null;

        var total = hours * 60 + minutes;
        return negative ? -total : total;
    }

    private static DateTime? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
            return null;

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }
}
