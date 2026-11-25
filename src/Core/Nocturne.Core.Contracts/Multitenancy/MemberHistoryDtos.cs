using System.Text.Json.Serialization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>What a change did. The two <c>Access</c> kinds are for a row whose detail was cleared.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemberHistoryKind>))]
public enum MemberHistoryKind
{
    RoleGiven,
    RoleTaken,
    PermissionGiven,
    PermissionTaken,

    /// <summary>A role or a permission was given. The row no longer says which.</summary>
    AccessGiven,

    /// <summary>A role or a permission was taken. The row no longer says which.</summary>
    AccessTaken,

    HistoryLimitChanged,
    RolePermissionsChanged,

    /// <summary>The date a member's access ends was set or moved. <see cref="MemberHistoryEntry.AccessEndsAt"/> is the new end.</summary>
    AccessEndSet,

    /// <summary>The date a member's access ends was removed.</summary>
    AccessEndCleared,

    /// <summary>The date a member's access ends changed. The row no longer says to what.</summary>
    AccessEndChanged,

    /// <summary>The member joined. <see cref="MemberHistoryEntry.JoinedHow"/> is null when the row no longer says how.</summary>
    Joined,

    /// <summary>The member stopped being a member. <see cref="MemberHistoryEntry.RemovedHow"/> is null when the row no longer says how.</summary>
    Removed,

    /// <summary>A team was given access to the site. <see cref="MemberHistoryEntry.Team"/> names it.</summary>
    TeamAccessGiven,

    /// <summary>A team's access to the site stopped. <see cref="MemberHistoryEntry.Team"/> names it.</summary>
    TeamAccessStopped,

    /// <summary>
    /// An owner changed what a team's access to the site gives. <see cref="MemberHistoryEntry.Team"/> names it, and
    /// <see cref="MemberHistoryEntry.Roles"/>, <see cref="MemberHistoryEntry.HistoryLimit"/> and
    /// <see cref="MemberHistoryEntry.AccessEndsAt"/> state what it gives after the change.
    /// </summary>
    TeamAccessChanged,

    /// <summary>The member was approved as staff of <see cref="MemberHistoryEntry.Team"/> at the site.</summary>
    TeamStaffApproved,

    /// <summary>The member was declined as staff of <see cref="MemberHistoryEntry.Team"/> at the site.</summary>
    TeamStaffDeclined,

    /// <summary>
    /// Whether <see cref="MemberHistoryEntry.Team"/> manages its own staff at the site changed.
    /// <see cref="MemberHistoryEntry.TeamManagesStaff"/> is the new value, null when the row no longer says.
    /// </summary>
    TeamStaffRuleChanged,
}

/// <summary>The history limit a member reads under after the change.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemberHistoryLimit>))]
public enum MemberHistoryLimit
{
    TwentyFourHours,
    Full,
}

/// <summary>Who made a change, when it was not a person.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemberHistoryActorKind>))]
public enum MemberHistoryActorKind
{
    /// <summary>A subject made the change. The actor carries its id and, while it exists, its name.</summary>
    Person,

    System,

    /// <summary>A caller with no credential at all.</summary>
    Anonymous,

    ApiKey,
    AccessToken,
    InstanceKey,
    GuestLink,
    PlatformAccess,

    /// <summary>The row names no actor, or a credential this reader does not recognise.</summary>
    Unknown,
}

/// <summary>A subject a change names. <paramref name="Name"/> is null once the subject no longer exists.</summary>
public record MemberHistoryPerson(Guid SubjectId, string? Name);

/// <summary>A team a change names: its name now, or the name the row recorded once the team is gone.</summary>
public record MemberHistoryTeam(Guid TeamId, string? Name);

/// <summary>The actor of a change. <paramref name="SubjectId"/> and <paramref name="Name"/> are set only for <see cref="MemberHistoryActorKind.Person"/>.</summary>
public record MemberHistoryActor(MemberHistoryActorKind Kind, Guid? SubjectId, string? Name);

/// <summary>
/// One change to a member's access. A field is null when it does not apply to the kind or the row
/// no longer records it.
/// </summary>
/// <param name="At">When the change was recorded.</param>
/// <param name="Member">The member the change was made to. Null for a change to a role's permissions, a team's access or its switch.</param>
/// <param name="Role">The role as recorded: its slug, for a member's role and for a tenant role's permission change.</param>
/// <param name="Gained">The permissions a role's change added.</param>
/// <param name="Lost">The permissions a role's change removed.</param>
/// <param name="AccessEndsAt">The instant a member's access ends after an <see cref="MemberHistoryKind.AccessEndSet"/> change, or a team's access ends, in UTC.</param>
/// <param name="ViaInvite">An invite caused the change.</param>
/// <param name="InviteLabel">The label of that invite, null when the invite is gone or has none.</param>
/// <param name="JoinedHow">How the member joined, for a <see cref="MemberHistoryKind.Joined"/> change.</param>
/// <param name="RemovedHow">How the membership ended, for a <see cref="MemberHistoryKind.Removed"/> change.</param>
/// <param name="By">The subject who let the member in, for a join by invite or by request, when the row names one.</param>
/// <param name="InRecordedJoin">
/// The change was made by an invite whose join has its own <see cref="MemberHistoryKind.Joined"/> change.
/// </param>
/// <param name="Team">The team, for a change to a team's access.</param>
/// <param name="Roles">The role slugs a team's access gives.</param>
/// <param name="TeamManagesStaff">Whether the team manages its own staff after a <see cref="MemberHistoryKind.TeamStaffRuleChanged"/> change.</param>
public record MemberHistoryEntry(
    Guid Id,
    DateTime At,
    MemberHistoryKind Kind,
    MemberHistoryPerson? Member,
    MemberHistoryActor Actor,
    string? Role,
    string? Permission,
    MemberHistoryLimit? HistoryLimit,
    IReadOnlyList<string>? Gained,
    IReadOnlyList<string>? Lost,
    DateTime? AccessEndsAt,
    bool ViaInvite,
    string? InviteLabel,
    MembershipJoinHow? JoinedHow,
    MembershipEndHow? RemovedHow,
    MemberHistoryPerson? By,
    bool InRecordedJoin,
    MemberHistoryTeam? Team,
    IReadOnlyList<string>? Roles,
    bool? TeamManagesStaff);

/// <summary>A page of <see cref="MemberHistoryEntry"/> changes, with the state of the member it is narrowed to.</summary>
public class MemberHistoryPage : PaginatedResponse<MemberHistoryEntry>
{
    /// <summary>
    /// When the access of the member the page is narrowed to ended, in UTC, if it has ended by its end
    /// date at the time of the read. Null when the page is not narrowed, the member's access has not
    /// ended, or the subject is not a member. A state, not a recorded change: nothing is written when
    /// the date passes.
    /// </summary>
    public DateTime? AccessEndedAt { get; set; }
}
