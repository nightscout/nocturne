using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Nocturne.API.Attributes;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Behavioural tests for <see cref="RequireScopeAttribute"/>, the filter that guards every
/// V1/V3 write endpoint. Verifies the exact security semantics relied on by the fix for the
/// unauthenticated-write bug: an anonymous request is rejected, a read-only grant cannot write,
/// and a matching read-write scope is accepted.
/// </summary>
public class RequireScopeAttributeTests
{
    private static IActionResult? Evaluate(RequireScopeAttribute attribute, bool authenticated, params string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        if (authenticated)
        {
            httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };
        }
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        attribute.OnAuthorization(filterContext);
        return filterContext.Result;
    }

    [Fact]
    public void UnauthenticatedRequestWithNoScopes_IsRejectedWith401()
    {
        // No resolved scopes means no grant at all: neither an authenticated caller nor a share.
        var result = Evaluate(new RequireScopeAttribute(Scope.GlucoseReadWrite),
            authenticated: false);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public void UnauthenticatedPublicShare_WithReadScopes_StillCannotWrite()
    {
        // The public-share path leaves AuthContext.IsAuthenticated = false while populating read
        // scopes, so the filter admits an unauthenticated caller for a read requirement — otherwise
        // every share link would 401. A write requirement is refused before the scope set is
        // consulted, which is what keeps write-immunity a property of the filter rather than of the
        // scopes a share happens to hold.
        var result = Evaluate(new RequireScopeAttribute(Scope.GlucoseReadWrite),
            authenticated: false, Scope.GlucoseRead);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public void UnauthenticatedPublicShare_WithReadScopes_CanRead()
    {
        var result = Evaluate(new RequireScopeAttribute(Scope.GlucoseRead),
            authenticated: false, Scope.GlucoseRead);

        result.Should().BeNull("public share links read the V1/V2/V3 surface");
    }

    [Fact]
    public void AuthenticatedReadOnlyGrant_CannotWrite_IsForbidden()
    {
        var result = Evaluate(new RequireScopeAttribute(Scope.GlucoseReadWrite),
            authenticated: true, Scope.GlucoseRead, Scope.TreatmentsRead);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public void AuthenticatedReadWriteGrant_CanWrite_IsAllowed()
    {
        var result = Evaluate(new RequireScopeAttribute(Scope.GlucoseReadWrite),
            authenticated: true, Scope.GlucoseReadWrite);

        result.Should().BeNull();
    }

    [Fact]
    public void FullAccessGrant_SatisfiesEveryWriteScope()
    {
        // A legacy full api-secret normalises to "*" (FullAccess) — real uploaders must keep working.
        Evaluate(new RequireScopeAttribute(Scope.GlucoseReadWrite), authenticated: true, Scope.FullAccess)
            .Should().BeNull();
        Evaluate(new RequireScopeAttribute(Scope.FullAccess), authenticated: true, Scope.FullAccess)
            .Should().BeNull();
    }

    [Fact]
    public void ReadWriteGrant_DoesNotReachAnotherCategory()
    {
        // A collection delete now gates on that collection's readwrite scope, so the boundary that
        // remains is the category one: treatments.readwrite must not reach the entries surface.
        var result = Evaluate(new RequireScopeAttribute(Scope.GlucoseReadWrite),
            authenticated: true, Scope.TreatmentsReadWrite);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public void ScopeRequiringFullAccess_IsNotSatisfiedByEveryReadWriteScope()
    {
        // Endpoints that still name "*" (tenant administration) are unreachable from the health-data
        // readwrite scopes however many of them a grant holds.
        var result = Evaluate(new RequireScopeAttribute(Scope.FullAccess),
            authenticated: true, [.. Scope.HealthReadWriteExpansion]);

        result.Should().BeOfType<ForbidResult>();
    }
}
