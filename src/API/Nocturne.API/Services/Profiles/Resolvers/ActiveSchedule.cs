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
    /// <param name="Entries">The schedule's entries, from the snapshot or the stored version.</param>
    /// <param name="SecondsFromMidnight">The local time of day to look the entries up at.</param>
    /// <param name="Adjustment">The percentage still to apply; its timeshift is already in <paramref name="SecondsFromMidnight"/>.</param>
    internal sealed record At<TEntry>(
        List<TEntry> Entries, int SecondsFromMidnight, CircadianAdjustment? Adjustment);

    /// <param name="timeMills">The time to resolve the schedule at, in Unix milliseconds.</param>
    /// <param name="specProfile">A profile named by the caller, read from the store in place of the active one.</param>
    /// <param name="active">Resolves the active profile's snapshot, name and adjustment at the time.</param>
    /// <param name="repo">The store the profile's schedule versions are read from.</param>
    /// <param name="versions">The cache the stored schedule and settings versions are read through.</param>
    /// <param name="storedEntries">Reads the entries of a stored schedule version.</param>
    /// <param name="snapshotEntries">Reads the entries of an activation's snapshot, or null when it carries none of this kind.</param>
    /// <param name="therapyRepo">The store the profile's timezone is read from.</param>
    /// <param name="patientRecordRepo">The store the patient's own timezone is read from.</param>
    /// <param name="ct">Cancels the operation.</param>
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
        var snapshot = specProfile is null ? await active.GetActiveSchedulesAsync(timeMills, ct) : null;
        var profileName = specProfile ?? await active.GetProfileNameAsync(timeMills, ct);
        var adjustment = await active.GetCircadianAdjustmentAsync(timeMills, ct);
        return await ResolveAsync(
            timeMills, profileName, snapshot, adjustment, repo, versions, storedEntries, snapshotEntries, therapyRepo,
            (await patientRecordRepo.GetAsync(ct))?.Timezone, ct);
    }

    /// <summary>
    /// The schedule at <paramref name="timeMills"/> of an active profile already resolved: its
    /// <paramref name="snapshot"/> when it carries one, else <paramref name="profileName"/>'s stored version then.
    /// </summary>
    /// <param name="timeMills">The time to resolve the schedule at, in Unix milliseconds.</param>
    /// <param name="profileName">The active profile's name, which the stored versions are read by.</param>
    /// <param name="snapshot">The schedules the active activation carries, if any.</param>
    /// <param name="adjustment">The activation's percentage and timeshift, if any.</param>
    /// <param name="repo">The store the profile's schedule versions are read from.</param>
    /// <param name="versions">The cache the stored schedule and settings versions are read through.</param>
    /// <param name="storedEntries">Reads the entries of a stored schedule version.</param>
    /// <param name="snapshotEntries">Reads the entries of the snapshot, or null when it carries none of this kind.</param>
    /// <param name="therapyRepo">The store the profile's timezone is read from.</param>
    /// <param name="patientTimezone">The patient's own timezone, which takes precedence over the profile's.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>Null when no schedule exists for the profile at the time.</returns>
    public static async Task<At<TEntry>?> ResolveAsync<TRecord, TEntry>(
        long timeMills,
        string profileName,
        ProfileActivationSnapshot? snapshot,
        CircadianAdjustment? adjustment,
        IProfileScopedRepository<TRecord> repo,
        ProfileVersionCache versions,
        Func<TRecord, List<TEntry>> storedEntries,
        Func<ProfileActivationSnapshot, List<TEntry>?> snapshotEntries,
        ITherapySettingsRepository therapyRepo,
        string? patientTimezone,
        CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;
        var entries = snapshot is not null
            ? snapshotEntries(snapshot)
            : await versions.GetActiveAtAsync(repo, profileName, timestamp, ct) is { } stored ? storedEntries(stored) : null;
        if (entries is null)
            return null;

        var shiftedMills = timeMills + (adjustment?.TimeshiftMs ?? 0);
        var timezone = ScheduleTimeHelper.TimezoneOf(
            patientTimezone,
            snapshot?.Timezone,
            (await versions.GetActiveAtAsync(therapyRepo, profileName, timestamp, ct))?.Timezone);
        var secondsFromMidnight = ScheduleEntryTime.SecondsOfDay(
            shiftedMills, TimeZoneHelper.GetTimeZoneInfoFromId(timezone));

        return new At<TEntry>(entries, secondsFromMidnight, adjustment);
    }
}
