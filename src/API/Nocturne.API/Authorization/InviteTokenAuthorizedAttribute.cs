namespace Nocturne.API.Authorization;

/// <summary>
/// Marks an action whose authorization comes from the invite token in its <c>{token}</c> route
/// value rather than from membership of the resolved tenant.
/// <para>
/// <see cref="Middleware.AuthenticationMiddleware"/> reads this off the endpoint metadata: when a
/// credential authenticates a subject who is not a member, the request is normally reduced to
/// anonymous. On an action carrying this attribute the middleware instead keeps the subject's
/// identity — and nothing else — provided the route's token names a currently valid invite of the
/// resolved tenant. Permissions, roles and granted scopes are all left empty, so the identity
/// buys the caller nothing beyond being nameable for the join.
/// </para>
/// <para>
/// Apply only to actions that take the invite token as a route value and act on that invite
/// alone. Every other endpoint keeps the membership requirement.
/// </para>
/// </summary>
/// <seealso cref="Middleware.AuthenticationMiddleware"/>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class InviteTokenAuthorizedAttribute : Attribute
{
    /// <summary>Route value holding the invite token on a marked action.</summary>
    public const string TokenRouteValue = "token";
}
