namespace Nocturne.Core.Models.Identity;

/// <summary>
/// Usage metadata for the tenants of an instance, for its operator: UTC calendar dates, counts,
/// configuration and coarse categories. It carries no time of day, no health value, no error text, no
/// client address or user agent, and no name or email.
/// </summary>
/// <param name="GeneratedAt">When the summary was read, in UTC.</param>
/// <param name="Tenants">One entry per tenant on the page, ordered by tenant id.</param>
public sealed record TenantUsageSummary(DateTime GeneratedAt, IReadOnlyList<TenantUsage> Tenants);

/// <summary>The usage metadata of one tenant.</summary>
public sealed record TenantUsage
{
    public required Guid TenantId { get; init; }

    public required string Slug { get; init; }

    public required bool IsActive { get; init; }

    /// <summary>The UTC date of the newest glucose reading, never its value.</summary>
    public DateOnly? LastReadingOn { get; init; }

    public DateOnly? OnboardingCompletedOn { get; init; }

    public DateOnly? SetupHubEnrolledOn { get; init; }

    /// <summary>
    /// The UTC date of the latest sign-in of any person who owns the tenant. A sign-in is per account,
    /// so an owner of several tenants moves this on each of them.
    /// </summary>
    public DateOnly? LastOwnerLoginOn { get; init; }

    /// <summary>The UTC date of the latest request on the tenant by any person who is a member.</summary>
    public DateOnly? LastMemberActivityOn { get; init; }

    /// <summary>
    /// People with live access, owners included. System subjects, the demo subject, deactivated
    /// accounts and ended memberships are not counted.
    /// </summary>
    public required int MemberCount { get; init; }

    /// <summary>The members counted in <see cref="MemberCount"/> who made a request in the last 7 days.</summary>
    public required int MembersActiveLast7Days { get; init; }

    public required IReadOnlyList<ConnectorUsage> Connectors { get; init; }

    public required int AlertRuleCount { get; init; }

    /// <summary>The distinct channel types of the tenant's alert rules, in order.</summary>
    public required IReadOnlyList<string> AlertChannelTypes { get; init; }

    /// <summary>Saved custom reports that are not deleted.</summary>
    public required int SavedReportCount { get; init; }

    public required int TrackerCount { get; init; }

    public required int ClockFaceCount { get; init; }

    /// <summary>Guest links that are neither revoked nor expired.</summary>
    public required int GuestLinkCount { get; init; }

    public required bool PublicShareEnabled { get; init; }

    public DateOnly? ShareLastAccessedOn { get; init; }

    /// <summary>One entry per kind of paired client app, with the latest date any of them was seen.</summary>
    public required IReadOnlyList<ClientDeviceUsage> ClientDevices { get; init; }

    /// <summary>Whether a chat account (Discord, Telegram, and so on) has an active link to the tenant.</summary>
    public required bool ChatIdentityLinked { get; init; }

    public required int OauthClientCount { get; init; }

    /// <summary>In-app hints that members of the tenant have seen or completed, summed over members.</summary>
    public required int CoachMarksSeenCount { get; init; }
}

/// <summary>A connector a tenant has configured, and its health.</summary>
/// <param name="Type">The connector id, such as <c>dexcom</c>, which names the vendor of the person's device.</param>
/// <param name="Enabled">Whether the connector is switched on.</param>
/// <param name="Status">Its sync state (<see cref="Connectors.ConnectorSyncStatus"/>), whether or not it is switched on.</param>
/// <param name="LastSuccessfulSyncOn">The UTC date a sync last completed.</param>
/// <param name="LastErrorOn">The UTC date of the current error; null once a sync succeeds.</param>
/// <param name="ErrorCategory">The current error, coarsely (<see cref="Connectors.ConnectorErrorCategory"/>).</param>
public sealed record ConnectorUsage(
    string Type,
    bool Enabled,
    string Status,
    DateOnly? LastSuccessfulSyncOn,
    DateOnly? LastErrorOn,
    string ErrorCategory);

/// <summary>A kind of paired client app, such as <c>companion</c>, and the UTC date one was last seen.</summary>
public sealed record ClientDeviceUsage(string Kind, DateOnly LastSeenOn);
