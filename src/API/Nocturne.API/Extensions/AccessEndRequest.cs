using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Extensions;

/// <summary>
/// The checks an end date asked for on a membership passes before it is stored, by an invite, a
/// request approval or a member edit.
/// </summary>
internal static class AccessEndRequest
{
    public const string PastDetail =
        "The end date must be in the future. To end access now, remove the member.";

    public const string OwnerDetail = "An owner's access cannot end on a date.";

    public const string ClearBeforeOwnerDetail =
        "This member's access ends on a date. Clear the date before you give them the owner role.";

    public const string SystemSubjectDetail = "This member is not a person, so its access cannot end on a date.";

    /// <summary>
    /// The instant <paramref name="requested"/> names, in UTC and cut to whole microseconds, the
    /// precision <c>timestamp with time zone</c> stores, so a value sent again compares equal to the
    /// stored one and moves nothing.
    /// </summary>
    public static DateTime? Instant(DateTimeOffset? requested) =>
        requested?.UtcDateTime is { } utc
            ? new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc)
            : null;

    /// <summary>
    /// Why <paramref name="accessEndsAt"/> may not be set by this caller, as a status and a detail,
    /// or null when it may: an end at or before <paramref name="now"/> is malformed (400), and one
    /// that outlasts the caller's own access is a refusal (403,
    /// <see cref="HttpContextExtensions.OutlastsCallerAccess"/>).
    /// </summary>
    public static (int Status, string Detail)? Refusal(HttpContext context, DateTime? accessEndsAt, DateTime now) =>
        accessEndsAt is { } end && MembershipLiveness.HasEnded(end, now)
            ? (StatusCodes.Status400BadRequest, PastDetail)
            : context.OutlastsCallerAccess(accessEndsAt)
                ? (StatusCodes.Status403Forbidden, HttpContextExtensions.AccessEndCeilingDetail)
                : null;
}
