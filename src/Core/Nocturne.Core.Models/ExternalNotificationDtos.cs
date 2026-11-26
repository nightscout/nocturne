using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// An informational notice from a sender outside Nocturne, for the notification tray of a tenant's
/// members. Plain text only. It is always shown as information: it raises no banner, no push and no
/// sound, and it has no buttons but its link and dismiss.
/// </summary>
public class ExternalNotificationRequest
{
    /// <summary>
    /// Who in the tenant receives it. The operator must name one. An app that names none sends it to
    /// the member who granted it, and only a grant made by an owner or an administrator may name one.
    /// </summary>
    public ExternalNotificationAudience? Audience { get; set; }

    /// <summary>
    /// The sender's own identifier for the notice: 1 to 100 letters, digits, <c>.</c>, <c>_</c>,
    /// <c>:</c> or <c>-</c>. A member receives each key from a sender once, however often it is sent,
    /// and it names the notice to withdraw.
    /// </summary>
    [Required]
    public string Key { get; set; } = string.Empty;

    /// <summary>The heading, at most 120 characters.</summary>
    [Required]
    public string Title { get; set; } = string.Empty;

    /// <summary>The text under the heading, at most 500 characters.</summary>
    [Required]
    public string Body { get; set; } = string.Empty;

    /// <summary>An https link the notice offers, which opens in a new tab.</summary>
    public ExternalNotificationLink? Link { get; set; }

    /// <summary>
    /// When the notice leaves the tray by itself. Defaults to 30 days from now and may be at most 90
    /// days from now.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>The link an external notice offers.</summary>
public class ExternalNotificationLink
{
    /// <summary>The button text, at most 40 characters.</summary>
    [Required]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// An absolute https URL on a domain the sender may link to: for the operator, a domain of
    /// <c>Operator:NotificationLinkDomains</c>; for an app, the domain of one of its https redirect
    /// URIs. A subdomain of an allowed domain is allowed.
    /// </summary>
    [Required]
    public string Url { get; set; } = string.Empty;
}

/// <summary>Who in a tenant an external notice goes to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExternalNotificationAudience>))]
public enum ExternalNotificationAudience
{
    /// <summary>The tenant's owners. A tenant with no owner receives it nowhere.</summary>
    [EnumMember(Value = "owners"), JsonStringEnumMemberName("owners")]
    Owners,

    /// <summary>Every member of the tenant who is a person and whose access has not ended.</summary>
    [EnumMember(Value = "members"), JsonStringEnumMemberName("members")]
    Members,
}

/// <summary>What sending an external notice did.</summary>
/// <param name="RecipientCount">How many members it is for; 0 when the audience is empty.</param>
/// <param name="Recipients">The outcome for each member.</param>
public sealed record ExternalNotificationSendResult(
    int RecipientCount,
    IReadOnlyList<ExternalNotificationRecipient> Recipients);

/// <summary>The outcome of an external notice for one member.</summary>
/// <param name="SubjectId">The member.</param>
/// <param name="Status">Whether the member has the notice.</param>
/// <param name="NotificationId">
/// The member's notification, when it still exists. Null for <see cref="ExternalNotificationDelivery.AlreadySent"/>.
/// </param>
public sealed record ExternalNotificationRecipient(
    Guid SubjectId,
    ExternalNotificationDelivery Status,
    Guid? NotificationId);

/// <summary>Whether a member has an external notice.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExternalNotificationDelivery>))]
public enum ExternalNotificationDelivery
{
    /// <summary>
    /// The member has it: this send created it, or an earlier send of the same key did and it still
    /// exists, read, dismissed or not.
    /// </summary>
    [EnumMember(Value = "created"), JsonStringEnumMemberName("created")]
    Created,

    /// <summary>
    /// An earlier send of the same key reached the member, and the notification has since been
    /// deleted. It is not sent again.
    /// </summary>
    [EnumMember(Value = "alreadySent"), JsonStringEnumMemberName("alreadySent")]
    AlreadySent,
}

/// <summary>What withdrawing an external notice did.</summary>
/// <param name="WithdrawnCount">How many members' notices this call withdrew.</param>
public sealed record ExternalNotificationWithdrawResult(int WithdrawnCount);
