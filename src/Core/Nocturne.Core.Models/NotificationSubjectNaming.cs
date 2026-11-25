namespace Nocturne.Core.Models;

/// <summary>How a notice that names a person points at that person.</summary>
public enum NoticeSubjectPointer
{
    /// <summary>The source id is the id of one of the person's membership requests.</summary>
    MembershipRequestId,

    /// <summary>The source id is the person's subject id.</summary>
    SubjectId,

    /// <summary>
    /// The notice's metadata holds the person's subject id under
    /// <see cref="NotificationSubjectNaming.MemberSubjectIdKey"/> or
    /// <see cref="NotificationSubjectNaming.AddedBySubjectIdKey"/>, written when the notice was.
    /// </summary>
    MetadataSubject,
}

/// <summary>
/// The notice types that put a person's name or own words in a notice held by someone else, and how
/// each notice points back at that person. Deleting the person's account removes these notices by
/// this declaration rather than by reading titles.
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

    /// <summary>
    /// Tells an owner that a channel of an enabled rule does not send. It carries the channel's label,
    /// which can name a contact, and the member it reaches and the member who added it.
    /// </summary>
    public const string AlertChannelNotSending = "alerts.channelNotSending";

    /// <summary>The metadata key of the member a notice is about.</summary>
    public const string MemberSubjectIdKey = "memberSubjectId";

    /// <summary>The metadata key of the member who added what a notice is about.</summary>
    public const string AddedBySubjectIdKey = "addedBySubjectId";

    public static IReadOnlyDictionary<string, NoticeSubjectPointer> Pointers { get; } =
        new Dictionary<string, NoticeSubjectPointer>(StringComparer.Ordinal)
        {
            [MembershipRequested] = NoticeSubjectPointer.MembershipRequestId,
            [AnonymousLoginRequest] = NoticeSubjectPointer.SubjectId,
            [AlertChannelNotSending] = NoticeSubjectPointer.MetadataSubject,
        };

    /// <summary>The types whose source id is one of the person's membership request ids.</summary>
    public static IReadOnlyList<string> PointingAtRequests { get; } = TypesPointing(NoticeSubjectPointer.MembershipRequestId);

    /// <summary>The types whose source id is the person's subject id.</summary>
    public static IReadOnlyList<string> PointingAtSubject { get; } = TypesPointing(NoticeSubjectPointer.SubjectId);

    /// <summary>The types whose metadata names the person.</summary>
    public static IReadOnlyList<string> PointingAtMetadata { get; } = TypesPointing(NoticeSubjectPointer.MetadataSubject);

    private static string[] TypesPointing(NoticeSubjectPointer pointer) =>
        [.. Pointers.Where(p => p.Value == pointer).Select(p => p.Key)];
}
