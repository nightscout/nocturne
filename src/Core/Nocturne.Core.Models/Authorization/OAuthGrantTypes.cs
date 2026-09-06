namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// The kinds of OAuth grant a token can be issued against. Stored verbatim on the grant row, so
/// these values are part of the persisted schema and must not change.
/// </summary>
/// <seealso cref="Scope.ValidateGrantScopes"/>
public static class OAuthGrantTypes
{
    /// <summary>Third-party application grant.</summary>
    public const string App = "app";

    /// <summary>User-to-user follower/caregiver sharing grant.</summary>
    public const string Follower = "follower";

    /// <summary>Direct token grant (API key style, no OAuth client).</summary>
    public const string Direct = "direct";

    /// <summary>Guest grant: temporary read-only access link.</summary>
    public const string Guest = "guest";
}
