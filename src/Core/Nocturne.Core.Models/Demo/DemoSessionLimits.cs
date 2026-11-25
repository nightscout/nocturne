namespace Nocturne.Core.Models.Demo;

/// <summary>
/// Bounds on the sessions held by a demo tenant's shared visitor account.
/// </summary>
/// <remarks>
/// In Core rather than beside the demo service because the cap is enforced in the data layer, at
/// the single place a <c>refresh_tokens</c> row is created — the demo sign-in endpoint is only one
/// of the paths that reaches it, and the anonymous, unrate-limited refresh endpoint rotates rows
/// through the same sink without passing through any demo code.
/// <para>
/// The sign-in endpoint's per-IP rate limit is not a substitute: it partitions on a caller-supplied
/// header (see the <c>demo-session</c> policy in <c>ServiceRegistrationExtensions</c>), while this
/// cap keys on the subject id.
/// </para>
/// </remarks>
public static class DemoSessionLimits
{
    /// <summary>
    /// Ceiling on live <c>refresh_tokens</c> rows for the demo subject.
    /// </summary>
    /// <remarks>
    /// Sized for concurrent real visitors, not for one visitor: the account is shared, so a trimmed
    /// session belongs to someone who has almost certainly left. Reaching the cap displaces the
    /// oldest rather than refusing the newest, because refusing would make the demo unusable for
    /// everyone as soon as it filled. Displacing costs little, because the access token is a
    /// self-contained JWT, and a read does not check its session. A visitor whose
    /// refresh row is trimmed keeps reading until that token expires, but can mint no credential in
    /// that time, since a credential is minted only for a caller whose session is live; their next
    /// refresh fails, at which point the login page signs them straight back in.
    /// </remarks>
    public const int MaxLiveSessions = 50;
}
