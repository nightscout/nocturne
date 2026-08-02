using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Nocturne.API.Attributes;
using Nocturne.API.Middleware;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// <see cref="RequireScopeAttribute"/> decides a read requirement on the resolved scope set alone,
/// because a public share link is deliberately <c>IsAuthenticated: false</c> while still carrying
/// scopes narrowed to <see cref="TenantPermissions.PublicShareScopes"/>. A requirement naming
/// anything other than read additionally demands an authenticated caller. These tests pin both
/// halves: the share can satisfy a read requirement, and it can never satisfy a write one.
/// </summary>
public class RequireScopeAttributeShareTests
{
    private static AuthorizationFilterContext Context(
        bool isAuthenticated, IEnumerable<string> grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = isAuthenticated };
        httpContext.Items["GrantedScopes"] =
            (IReadOnlySet<string>)grantedScopes.ToHashSet(StringComparer.Ordinal);

        return new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            []);
    }

    /// <summary>The scopes AuthenticationMiddleware resolves for a share granted every category.</summary>
    private static IReadOnlySet<string> FullShareScopes() =>
        TenantPermissions.PublicShareScopes.ToHashSet(StringComparer.Ordinal);

    [Theory]
    [InlineData(OAuthScopes.GlucoseRead)]
    [InlineData(OAuthScopes.TreatmentsRead)]
    [InlineData(OAuthScopes.DevicesRead)]
    [InlineData(OAuthScopes.FoodRead)]
    public void AnonymousShare_SatisfiesAReadScopeItWasGranted(string requiredScope)
    {
        var context = Context(isAuthenticated: false, FullShareScopes());

        new RequireScopeAttribute(requiredScope).OnAuthorization(context);

        context.Result.Should().BeNull(
            "a share link must keep reading the categories its owner granted");
    }

    [Fact]
    public void DefaultGlucoseOnlyShare_SatisfiesTheGlucoseReadEndpoints()
    {
        // DefaultPublicShareScopes is what a freshly-enabled share link carries.
        var context = Context(isAuthenticated: false, TenantPermissions.DefaultPublicShareScopes);

        new RequireScopeAttribute(OAuthScopes.GlucoseRead).OnAuthorization(context);

        context.Result.Should().BeNull();
    }

    [Theory]
    [InlineData(OAuthScopes.GlucoseReadWrite)]
    [InlineData(OAuthScopes.TreatmentsReadWrite)]
    [InlineData(OAuthScopes.DevicesReadWrite)]
    [InlineData(OAuthScopes.FoodReadWrite)]
    [InlineData(OAuthScopes.TherapyReadWrite)]
    [InlineData(OAuthScopes.AlertsReadWrite)]
    [InlineData(OAuthScopes.FullAccess)]
    public void AnonymousShare_NeverSatisfiesAWriteScope(string writeScope)
    {
        var context = Context(isAuthenticated: false, FullShareScopes());

        new RequireScopeAttribute(writeScope).OnAuthorization(context);

        context.Result.Should().BeOfType<UnauthorizedResult>(
            "an unauthenticated caller is refused before its scopes are consulted, so a write "
            + "requirement can never be reached by a share");
    }

    [Theory]
    [InlineData(OAuthScopes.TherapyRead)]
    [InlineData(OAuthScopes.AlertsRead)]
    [InlineData(OAuthScopes.SleepRead)]
    public void AnonymousShare_IsDeniedCategoriesOutsideTheShareSet(string requiredScope)
    {
        // therapy/alerts/sleep are not shareable categories, and the share RLS policies already
        // hide those tables from a share, so denial here matches what the data layer does.
        var context = Context(isAuthenticated: false, FullShareScopes());

        new RequireScopeAttribute(requiredScope).OnAuthorization(context);

        context.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public void AnonymousCallerWithNoScopes_IsUnauthorized()
    {
        var context = Context(isAuthenticated: false, []);

        new RequireScopeAttribute(OAuthScopes.GlucoseRead).OnAuthorization(context);

        context.Result.Should().BeOfType<UnauthorizedResult>(
            "no resolved scopes means no grant at all — the check fails closed");
    }

    [Fact]
    public void AuthenticatedCallerWithNoScopes_IsForbidden()
    {
        var context = Context(isAuthenticated: true, []);

        new RequireScopeAttribute(OAuthScopes.GlucoseRead).OnAuthorization(context);

        context.Result.Should().BeOfType<ForbidResult>(
            "the identity is known, so the failure is authorization, not authentication");
    }

    /// <summary>
    /// The legacy uploader plane (AAPS/Loop/Trio/iAPS/xDrip+) authenticates with a pre-hashed
    /// api-secret or a legacy access token, which AuthenticationMiddleware translates through
    /// <see cref="ScopeTranslator.FromPermissions"/>. Every read scope the V1/V2/V3 endpoints now
    /// require must survive that translation, or those clients break.
    /// </summary>
    [Theory]
    [InlineData(OAuthScopes.GlucoseRead)]
    [InlineData(OAuthScopes.TreatmentsRead)]
    [InlineData(OAuthScopes.DevicesRead)]
    [InlineData(OAuthScopes.TherapyRead)]
    [InlineData(OAuthScopes.FoodRead)]
    [InlineData(OAuthScopes.AlertsRead)]
    [InlineData(OAuthScopes.HeartRateRead)]
    [InlineData(OAuthScopes.StepCountRead)]
    [InlineData(OAuthScopes.SleepRead)]
    public void LegacyApiSecretAndReadTokens_SatisfyEveryRequiredReadScope(string requiredScope)
    {
        // api-secret / admin subjects carry "*"; read-only legacy subjects carry the Shiro
        // wildcard read or the named "readable" role.
        foreach (var permissions in new[]
                 {
                     new[] { "*" },
                     new[] { "api:*" },
                     new[] { "api:*:read" },
                     new[] { "readable" },
                 })
        {
            var scopes = ScopeTranslator.FromPermissions(permissions);
            var context = Context(isAuthenticated: true, scopes);

            new RequireScopeAttribute(requiredScope).OnAuthorization(context);

            context.Result.Should().BeNull(
                $"permissions [{string.Join(",", permissions)}] must still satisfy {requiredScope}");
        }
    }

    /// <summary>
    /// <c>/api/v1/activity</c> merges four storages, so it admits on an OR over their read scopes and
    /// then filters the response per record. A legacy read subject carries the permissions the
    /// <c>readable</c> seed role grants, and those must resolve to all four categories or the legacy
    /// activity read silently starts returning a subset.
    /// </summary>
    [Fact]
    public void LegacyReadableRolePermissions_ReadEveryMergedActivityCategory()
    {
        // The permission strings RoleService seeds for the "readable" and "public" roles.
        var scopes = ScopeTranslator.FromPermissions([
            "api:entries:read",
            "api:treatments:read",
            "api:devicestatus:read",
            "api:profile:read",
            "api:food:read",
            "api:activity:read",
            "api:trackers:read",
        ]);

        foreach (var category in new[]
                 {
                     OAuthScopes.TreatmentsRead,
                     OAuthScopes.HeartRateRead,
                     OAuthScopes.StepCountRead,
                     OAuthScopes.SleepRead,
                 })
        {
            OAuthScopes.SatisfiesScope(scopes, category).Should().BeTrue(
                $"a legacy read subject must keep reading {category} data from /api/v1/activity");
        }
    }

    /// <summary>
    /// A per-collection legacy token must satisfy its own collection and nothing else — this is the
    /// vulnerability the read gating closes.
    /// </summary>
    [Fact]
    public void LegacySingleCollectionToken_ReadsOnlyItsOwnCollection()
    {
        var scopes = ScopeTranslator.FromPermissions(["api:entries:read"]);
        var granted = Context(isAuthenticated: true, scopes);
        var denied = Context(isAuthenticated: true, scopes);

        new RequireScopeAttribute(OAuthScopes.GlucoseRead).OnAuthorization(granted);
        new RequireScopeAttribute(OAuthScopes.TherapyRead).OnAuthorization(denied);

        granted.Result.Should().BeNull();
        denied.Result.Should().BeOfType<ForbidResult>(
            "an entries-scoped token must not read therapy settings");
    }
}
