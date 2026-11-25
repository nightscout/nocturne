namespace Nocturne.Core.Models;

/// <summary>How the source id of a notice that names a person points at that person.</summary>
public enum NoticeSubjectPointer
{
    /// <summary>The source id is the id of one of the person's membership requests.</summary>
    MembershipRequestId,

    /// <summary>The source id is the person's subject id.</summary>
    SubjectId,
}

/// <summary>
/// The notice types that put a person's name or own words in a notice held by someone else, and how
/// each notice points back at that person. Deleting the person's account removes these notices by
/// this declaration rather than by reading titles or metadata.
/// </summary>
/// <remarks>
/// A type missing here is left alone when the person is deleted. <see cref="NotificationTemplate.NamesSubject"/>
/// is the template's statement of the same fact, and a test holds the two together.
/// </remarks>
public static class NotificationSubjectNaming
{
    /// <summary>
    /// Tells a member who can decide it that a request was filed. The title names the requester and
    /// the subtitle is the requester's message.
    /// </summary>
    public const string MembershipRequested = "membership.requested";

    /// <summary>Tells an owner that a person asked to sign in. The notice is about the person.</summary>
    public const string AnonymousLoginRequest = "passkey.anonymous_login_request";

    public static IReadOnlyDictionary<string, NoticeSubjectPointer> Pointers { get; } =
        new Dictionary<string, NoticeSubjectPointer>(StringComparer.Ordinal)
        {
            [MembershipRequested] = NoticeSubjectPointer.MembershipRequestId,
            [AnonymousLoginRequest] = NoticeSubjectPointer.SubjectId,
        };

    /// <summary>The types whose source id is one of the person's membership request ids.</summary>
    public static IReadOnlyList<string> PointingAtRequests { get; } = TypesPointing(NoticeSubjectPointer.MembershipRequestId);

    /// <summary>The types whose source id is the person's subject id.</summary>
    public static IReadOnlyList<string> PointingAtSubject { get; } = TypesPointing(NoticeSubjectPointer.SubjectId);

    private static string[] TypesPointing(NoticeSubjectPointer pointer) =>
        [.. Pointers.Where(p => p.Value == pointer).Select(p => p.Key)];
}
