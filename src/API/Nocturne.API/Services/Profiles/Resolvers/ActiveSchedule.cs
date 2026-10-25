using Nocturne.Core.Clinical.Schedules;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// The schedule a point resolver reads at a time: the active activation's snapshot when it carries
/// one (<see cref="IActiveProfileResolver.GetActiveSchedulesAsync"/>), else the stored profile's
/// version at that time, with the local time of day the entries are looked up at.
/// </summary>
internal static class ActiveSchedule
{
    /// <param name="Adjustment">The percentage still to apply; its timeshift is already in <paramref name="SecondsFromMidnight"/>.</param>
    internal sealed record At<TEntry>(
        List<TEntry> Entries, int SecondsFromMidnight, CircadianAdjustment? Adjustment);

    /// <param name="specProfile">A profile named by the caller, read from the store in place of the active one.</param>
    /// <returns>Null when no schedule exists for the profile at the time.</returns>
    public static async Task<At<TEntry>?> ResolveAsync<TRecord, TEntry>(
        long timeMills,
        string? specProfile,
        IActiveProfileResolver active,
        IProfileScopedRepository<TRecord> repo,
        ProfileVersionCache versions,
        Func<TRecord, List<TEntry>> storedEntries,
        Func<ProfileActivationSnapshot, List<TEntry>?> snapshotEntries,
        ITherapySettingsRepository therapyRepo,
        IPatientRecordRepository patientRecordRepo,
        CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;
        var snapshot = specProfile is null ? await active.GetActiveSchedulesAsync(timeMills, ct) : null;
        var profileName = specProfile ?? await active.GetProfileNameAsync(timeMills, ct);

        var entries = snapshot is not null
            ? snapshotEntries(snapshot)
            : await versions.GetActiveAtAsync(repo, profileName, timestamp, ct) is { } stored ? storedEntries(stored) : null;
        if (entries is null)
            return null;

        var adjustment = await active.GetCircadianAdjustmentAsync(timeMills, ct);
        var shiftedMills = timeMills + (adjustment?.TimeshiftMs ?? 0);

        var timezone = ScheduleTimeHelper.TimezoneOf(
            (await patientRecordRepo.GetAsync(ct))?.Timezone,
            snapshot?.Timezone,
            (await versions.GetActiveAtAsync(therapyRepo, profileName, timestamp, ct))?.Timezone);
        var secondsFromMidnight = ScheduleEntryTime.SecondsOfDay(
            shiftedMills, TimeZoneHelper.GetTimeZoneInfoFromId(timezone));

        return new At<TEntry>(entries, secondsFromMidnight, adjustment);
    }
}
