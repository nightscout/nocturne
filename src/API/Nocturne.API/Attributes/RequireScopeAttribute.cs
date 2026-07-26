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

        if (!httpContext.IsAuthenticated())
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var grantedScopes = httpContext.GetGrantedScopes();

        var hasSufficientScope = _requireAll
            ? _requiredScopes.All(s => OAuthScopes.SatisfiesScope(grantedScopes, s))
            : _requiredScopes.Any(s => OAuthScopes.SatisfiesScope(grantedScopes, s));

        if (!hasSufficientScope)
        {
            context.Result = new ForbidResult();
        }
    }
}
