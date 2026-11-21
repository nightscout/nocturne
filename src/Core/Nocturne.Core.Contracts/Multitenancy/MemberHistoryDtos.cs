using System.Text.Json.Serialization;

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

/// <summary>The actor of a change. <paramref name="SubjectId"/> and <paramref name="Name"/> are set only for <see cref="MemberHistoryActorKind.Person"/>.</summary>
public record MemberHistoryActor(MemberHistoryActorKind Kind, Guid? SubjectId, string? Name);

/// <summary>
/// One change to a member's access. A field is null when it does not apply to the kind or the row
/// no longer records it.
/// </summary>
/// <param name="At">When the change was recorded.</param>
/// <param name="Member">The member the change was made to. Null for a change to a role's permissions.</param>
/// <param name="Role">The role as recorded: its slug, for a member's role and for a tenant role's permission change.</param>
/// <param name="Gained">The permissions a role's change added.</param>
/// <param name="Lost">The permissions a role's change removed.</param>
/// <param name="ViaInvite">An invite caused the change.</param>
/// <param name="InviteLabel">The label of that invite, null when the invite is gone or has none.</param>
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
    bool ViaInvite,
    string? InviteLabel);
