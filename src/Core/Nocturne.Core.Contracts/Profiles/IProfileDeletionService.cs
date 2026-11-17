using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Profiles;

/// <summary>
/// Why a named-profile delete was refused, or <see cref="None"/> when it went ahead.
/// </summary>
public enum ProfileDeletionRefusal
{
    /// <summary>The delete is allowed.</summary>
    None,

    /// <summary>No profile carries that name, compared case-sensitively.</summary>
    NotFound,

    /// <summary>
    /// The only profile the tenant has. Removing it would leave therapy reads with nothing to
    /// resolve against, so the caller has to have a second profile before dropping this one.
    /// </summary>
    OnlyProfile,

    /// <summary>
    /// The profile currently flagged active. The caller sets another profile active first, which
    /// keeps the choice of successor with them rather than having the delete pick one.
    /// </summary>
    ActiveProfile,
}

/// <summary>The result of a named-profile delete.</summary>
/// <param name="Refusal">Why nothing was deleted, or <see cref="ProfileDeletionRefusal.None"/>.</param>
/// <param name="DeletedRecords">Rows soft-deleted across the five profile-scoped tables.</param>
public readonly record struct ProfileDeletionResult(
    ProfileDeletionRefusal Refusal,
    int DeletedRecords
)
{
    /// <summary>True when the delete ran.</summary>
    public bool Deleted => Refusal == ProfileDeletionRefusal.None;
}

/// <summary>
/// Deletes a whole named therapy profile: every <see cref="TherapySettings"/>,
/// <see cref="BasalSchedule"/>, <see cref="CarbRatioSchedule"/>, <see cref="SensitivitySchedule"/>
/// and <see cref="TargetRangeSchedule"/> row carrying that profile name.
/// </summary>
/// <remarks>
/// A profile name is not a row — it is the key five tables share, one row per name per upstream
/// upload document. Nothing else in the API removes that whole set. The per-record deletes take a
/// single id. The V3 profile delete keys on the upload document, so it takes every *other* name in
/// that upload with it.
/// </remarks>
public interface IProfileDeletionService
{
    /// <summary>
    /// Soft-deletes every profile-scoped record whose profile name matches
    /// <paramref name="profileName"/> exactly, in one transaction.
    /// </summary>
    /// <param name="profileName">
    /// The profile name, matched case-sensitively — <c>Default</c> and <c>default</c> are two
    /// profiles, which is the case this exists for.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProfileDeletionResult> DeleteByProfileNameAsync(
        string profileName,
        CancellationToken ct = default
    );

    /// <summary>
    /// The guard <see cref="DeleteByProfileNameAsync"/> applies, over therapy settings ordered
    /// newest-first. Separated from the delete so the rule can be exercised without a database.
    /// </summary>
    /// <param name="therapySettingsNewestFirst">Every live therapy settings row, newest first.</param>
    /// <param name="profileName">The profile name the caller asked to delete.</param>
    static ProfileDeletionRefusal Evaluate(
        IEnumerable<TherapySettings> therapySettingsNewestFirst,
        string profileName
    )
    {
        var records = therapySettingsNewestFirst.ToList();

        var names = records
            .Select(ts => ts.ProfileName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (!names.Contains(profileName, StringComparer.Ordinal))
            return ProfileDeletionRefusal.NotFound;

        if (names.Count <= 1)
            return ProfileDeletionRefusal.OnlyProfile;

        // Newest-first, so the first row for the name holds the flag the UI renders and
        // SetDefaultProfile maintains. Older rows keep whatever flag they were written with.
        var newest = records.First(ts =>
            string.Equals(ts.ProfileName, profileName, StringComparison.Ordinal)
        );

        return newest.IsDefault ? ProfileDeletionRefusal.ActiveProfile : ProfileDeletionRefusal.None;
    }
}
