using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Attributes;

/// <summary>
/// Attribute to require specific OAuth scopes for controller actions.
/// Composes with the existing <see cref="RequirePermissionAttribute"/>; both can be used
/// on the same endpoint. This attribute checks the resolved scopes on
/// the <see cref="AuthContext"/> (populated by <see cref="Middleware.AuthenticationMiddleware"/>
/// from either OAuth tokens or translated legacy permissions via <see cref="OAuthScopes"/>).
/// </summary>
/// <remarks>
/// Scope satisfaction is evaluated by <see cref="OAuthScopes.SatisfiesScope"/> which supports
/// hierarchical scope matching (e.g., <c>read</c> satisfies <c>read:entries</c>).
/// The granted scopes are further refined by <see cref="Middleware.MemberScopeMiddleware"/>
/// based on the user's tenant membership roles.
/// <para>
/// The check keys off the resolved scope set rather than an authenticated identity, so it also
/// applies to the anonymous public-share principal, whose scopes are narrowed to
/// <see cref="TenantPermissions.PublicShareScopes"/>. An empty scope set is rejected.
/// </para>
/// </remarks>
/// <seealso cref="RequirePermissionAttribute"/>
/// <seealso cref="Middleware.AuthenticationMiddleware"/>
/// <seealso cref="Middleware.MemberScopeMiddleware"/>
/// <seealso cref="OAuthScopes"/>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireScopeAttribute : Attribute, IAuthorizationFilter
{
    private readonly string[] _requiredScopes;
    private readonly bool _requireAll;

    /// <summary>
    /// The scopes this attribute requires, in declaration order. Read by the guard test that asserts
    /// each write action requires the scope for its data category.
    /// </summary>
    public IReadOnlyList<string> Scopes => _requiredScopes;

    /// <summary>
    /// Require one or more OAuth scopes.
    /// By default, any one of the listed scopes is sufficient (OR logic).
    /// </summary>
    /// <param name="scopes">Required scope(s)</param>
    public RequireScopeAttribute(params string[] scopes)
    {
        _requiredScopes = scopes;
        _requireAll = false;
    }

    /// <summary>
    /// Require one or more OAuth scopes with explicit AND/OR control.
    /// </summary>
    /// <param name="requireAll">True = all scopes required (AND), false = any one sufficient (OR)</param>
    /// <param name="scopes">Required scope(s)</param>
    public RequireScopeAttribute(bool requireAll, params string[] scopes)
    {
        _requiredScopes = scopes;
        _requireAll = requireAll;
    }

    /// <summary>
    /// Evaluates the scope requirement against the current request's granted scopes.
    /// </summary>
    /// <param name="context">The authorization filter context.</param>
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var httpContext = context.HttpContext;

        var grantedScopes = httpContext.GetGrantedScopes();

        // Eligibility is the presence of resolved scopes, not an authenticated identity. A public
        // share link ({token}.share.{baseDomain}) is deliberately IsAuthenticated: false, yet
        // AuthenticationMiddleware resolves its Public subject down to
        // TenantPermissions.PublicShareScopes and publishes them here. Requiring authentication
        // would 401 every share, so the scope set is the gate for both principals.
        //
        // This cannot widen writes: PublicShareScopes contains only ".read" scopes, and
        // OAuthScopes.SatisfiesScope grants a required scope solely on an exact match, on "*", or
        // on the matching ".readwrite" — so no share scope set can ever satisfy a write scope.
        //
        // No scopes at all means no resolved grant, which fails closed. An authenticated caller
        // gets 403 (identity known, grant insufficient); anyone else gets 401.
        if (grantedScopes.Count == 0)
        {
            context.Result = httpContext.IsAuthenticated()
                ? new ForbidResult()
                : new UnauthorizedResult();
            return;
        }

        var hasSufficientScope = _requireAll
            ? _requiredScopes.All(s => OAuthScopes.SatisfiesScope(grantedScopes, s))
            : _requiredScopes.Any(s => OAuthScopes.SatisfiesScope(grantedScopes, s));

        if (!hasSufficientScope)
        {
            context.Result = new ForbidResult();
        }
    }
}
