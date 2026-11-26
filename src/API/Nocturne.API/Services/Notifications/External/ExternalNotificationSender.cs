namespace Nocturne.API.Services.Notifications.External;

/// <summary>Who sends an external notice.</summary>
public enum ExternalSenderKind
{
    /// <summary>The operator of the instance, authenticated by the instance key.</summary>
    Operator,

    /// <summary>A connected app holding <c>notifications.send</c>.</summary>
    App,
}

/// <summary>
/// A sender of external notices, as the notices show it and the ledger keys it.
/// </summary>
/// <param name="Kind">Operator or app. Decided by the route the notice came in on, never by the name.</param>
/// <param name="Id"><see cref="OperatorId"/> for the operator, the OAuth <c>client_id</c> for an app.</param>
/// <param name="Name">The operator's name, or the app's registered name.</param>
/// <param name="GrantId">The OAuth grant an app sends under; null for the operator.</param>
/// <param name="LinkDomains">The domains its notices may link to.</param>
public sealed record ExternalNotificationSender(
    ExternalSenderKind Kind,
    string Id,
    string Name,
    Guid? GrantId,
    IReadOnlyList<string> LinkDomains)
{
    public const string OperatorId = "operator";

    /// <summary>The notification type of the operator's notices.</summary>
    public const string OperatorType = "external.operator";

    /// <summary>The notification type of an app's notices.</summary>
    public const string AppType = "external.app";

    /// <summary>The metadata key the sender's kind is stored under: <c>operator</c> or <c>app</c>.</summary>
    public const string SenderKindKey = "senderKind";

    /// <summary>The metadata key the sender's name is stored under.</summary>
    public const string SenderNameKey = "senderName";

    /// <summary>The metadata key the link's URL is stored under.</summary>
    public const string LinkUrlKey = "linkUrl";

    /// <summary>The metadata key the link's label is stored under.</summary>
    public const string LinkLabelKey = "linkLabel";

    /// <summary>How many new keys an app may send to one tenant in a day.</summary>
    public const int AppDailyLimit = 5;

    public static ExternalNotificationSender Operator(string name, IReadOnlyList<string> linkDomains) =>
        new(ExternalSenderKind.Operator, OperatorId, name, null, linkDomains);

    public static ExternalNotificationSender App(string clientId, string name, Guid? grantId, IReadOnlyList<string> linkDomains) =>
        new(ExternalSenderKind.App, clientId, name, grantId, linkDomains);

    /// <summary>The kind as the ledger and the notice metadata store it.</summary>
    public string KindName => Kind == ExternalSenderKind.Operator ? "operator" : "app";

    /// <summary>The in-app notification type of this sender's notices.</summary>
    public string NotificationType => Kind == ExternalSenderKind.Operator ? OperatorType : AppType;

    /// <summary>
    /// The in-app notification source of this sender's notices, so the per-source cap of active
    /// notices applies to each sender on its own.
    /// </summary>
    public string NotificationSource => Kind == ExternalSenderKind.Operator ? OperatorId : $"app:{Id}";

    /// <summary>Senders on new keys are capped per tenant per day; the operator is not.</summary>
    public int? DailyLimit => Kind == ExternalSenderKind.Operator ? null : AppDailyLimit;
}
