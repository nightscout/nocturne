using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Attributes;

/// <summary>
/// Attribute to require specific OAuth scopes for controller actions.
/// Composes with the existing RequirePermission attribute; both can be used
/// on the same endpoint. This attribute checks the resolved scopes on
/// the AuthContext (populated by the auth middleware from either OAuth tokens
/// or translated legacy permissions).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireScopeAttribute : Attribute, IAuthorizationFilter
{
    private readonly string[] _requiredScopes;
    private readonly bool _requireAll;

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
