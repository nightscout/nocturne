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
/// from either OAuth tokens or translated legacy permissions via <see cref="Scope"/>).
/// </summary>
/// <remarks>
/// Scope satisfaction is evaluated by <see cref="Scope.Satisfies"/> which supports
/// hierarchical scope matching (e.g., <c>read</c> satisfies <c>read:entries</c>).
/// The granted scopes are further refined by <see cref="Middleware.MemberScopeMiddleware"/>
/// based on the user's tenant membership roles.
/// <para>
/// A read requirement is decided on the resolved scope set alone, so it admits the anonymous
/// public-share principal, whose scopes are narrowed to
/// <see cref="Scope.PublicShareScopes"/>. A requirement naming anything other than
/// read also requires an authenticated caller, so no anonymous principal can pass a write gate
/// however its scopes are resolved. An empty scope set is rejected either way.
/// </para>
/// </remarks>
/// <seealso cref="RequirePermissionAttribute"/>
/// <seealso cref="Middleware.AuthenticationMiddleware"/>
/// <seealso cref="Middleware.MemberScopeMiddleware"/>
/// <seealso cref="Scope"/>
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

    /// <summary>The scopes this attribute requires.</summary>
    public IReadOnlyList<string> RequiredScopes => _requiredScopes;

    /// <summary>Whether every scope in <see cref="RequiredScopes"/> is required (AND) or any one (OR).</summary>
    public bool RequiresAll => _requireAll;

    /// <summary>
    /// Evaluates the scope requirement against the current request's granted scopes.
    /// </summary>
    /// <param name="context">The authorization filter context.</param>
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var httpContext = context.HttpContext;

        var grantedScopes = httpContext.GetGrantedScopes();

        // An unauthenticated caller is eligible only for a read requirement. A public share link
        // ({token}.share.{baseDomain}) is deliberately IsAuthenticated: false, yet
        // AuthenticationMiddleware resolves its Public subject down to
        // Scope.PublicShareScopes and publishes them here, so requiring authentication
        // outright would 401 every share.
        //
        // Restricting it to read requirements keeps "an unauthenticated principal can never pass a
        // write gate" a property of this attribute. Deriving it from the scopes a share happens to
        // hold would instead make it depend on every present and future anonymous path publishing
        // only read scopes, and the capability atoms device.notify and device.actuate are matched
        // exactly — they are neither a read nor a write of a data category.
        if (!httpContext.IsAuthenticated() && !_requiredScopes.All(Scope.IsReadScope))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

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
            ? _requiredScopes.All(s => Scope.Satisfies(grantedScopes, s))
            : _requiredScopes.Any(s => Scope.Satisfies(grantedScopes, s));

        if (!hasSufficientScope)
        {
            context.Result = new ForbidResult();
        }
    }
}
