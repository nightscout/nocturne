namespace Nocturne.Core.Models.Identity;

/// <summary>One team at one site: what an owner's notice about the team's staff is for.</summary>
public readonly record struct TeamSiteKey(Guid TenantId, Guid TeamId);

/// <summary>
/// The notices to owners that a committed team write owes, for each team at each site:
/// <paramref name="Recount"/> the staff who wait there, since the write can change how many do, and
/// <paramref name="Joined"/> that the team's staff newly reach the site.
/// </summary>
public sealed record StaffNotices(IReadOnlyList<TeamSiteKey> Recount, IReadOnlyList<TeamSiteKey> Joined)
{
    /// <summary>A write that owes none.</summary>
    public static StaffNotices None { get; } = new([], []);

    /// <summary>The sites these notices are for, in ascending id.</summary>
    public IReadOnlyList<Guid> Sites =>
        Recount.Select(k => k.TenantId).Concat(Joined.Select(k => k.TenantId)).Distinct().Order().ToList();
}

/// <summary>
/// A team's current name, and the staff whose rows at a site wait for an owner's approval, as the database
/// holds them now.
/// </summary>
public sealed record TeamStaffWaiting(string TeamName, IReadOnlyList<Guid> WaitingSubjectIds);
