using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Attributes;
using Nocturne.API.Middleware;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// End-to-end authorization proof: the real <see cref="MemberScopeMiddleware"/> feeding the real
/// <see cref="RequireScopeAttribute"/> authorization filter, over every scope-gated action the API
/// assembly declares. Unit-testing the middleware's output set proves what it resolves; this proves
/// the gates downstream of it actually open, which is the property that regressed — every
/// <c>[RequireScope]</c> action returned 403 for every non-owner member of every tenant.
/// </summary>
/// <remarks>
/// The gated actions are discovered by reflection rather than listed, so a newly gated controller is
/// covered without editing this file and cannot silently fall outside the proof.
/// </remarks>
[Trait("Category", "Unit")]
public class MemberScopeFilterPipelineTests
{
    /// <summary>
    /// A scope-gated action: the declaring controller, the method, and the filter instances that
    /// guard it (method-level attributes plus any class-level attribute).
    /// </summary>
    private sealed record GatedAction(
        string Controller, string Action, RequireScopeAttribute[] Filters, bool RequiresFullAccess);

    private static IEnumerable<GatedAction> DiscoverGatedActions()
    {
        var controllers = typeof(RequireScopeAttribute).Assembly
            .GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type)
                           && type is { IsAbstract: false, IsPublic: true });

        foreach (var controller in controllers)
        {
            var classFilters = controller
                .GetCustomAttributes<RequireScopeAttribute>(inherit: true)
                .ToArray();

            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                                | BindingFlags.DeclaredOnly);

            foreach (var action in actions)
            {
                if (action.IsSpecialName) continue;

                var methodFilters = action.GetCustomAttributes<RequireScopeAttribute>(inherit: true)
                    .ToArray();
                var filters = methodFilters.Concat(classFilters).ToArray();
                if (filters.Length == 0) continue;

                // Delete endpoints are gated on "*" by design: an Administrator is deliberately not
                // a superuser, so these must stay closed and act as the negative control.
                var requiresFullAccess = RequiredScopesOf(filters).Contains(OAuthScopes.FullAccess);

                yield return new GatedAction(
                    controller.Name, action.Name, filters, requiresFullAccess);
            }
        }
    }

    private static IReadOnlySet<string> RequiredScopesOf(IEnumerable<RequireScopeAttribute> filters)
    {
        // The required scopes are private state on the attribute; read them the same way the
        // framework would not have to, so the negative control keys off the real values.
        var scopes = new HashSet<string>();
        foreach (var filter in filters)
        {
            var field = typeof(RequireScopeAttribute)
                .GetField("_requiredScopes", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(filter) is string[] required)
                scopes.UnionWith(required);
        }
        return scopes;
    }

    [Fact]
    public void DiscoversTheGatedSurface()
    {
        // Guards the proof itself: a reflection change that silently found nothing would make every
        // assertion below vacuous.
        var gated = DiscoverGatedActions().ToList();

        gated.Should().HaveCountGreaterThan(50);
        gated.Select(g => g.Controller).Should().Contain(
        [
            "ClientDevicesController", "HeartRateController", "StepCountController",
            "SleepReportController", "ApsSnapshotController", "PumpSnapshotController",
            "UploaderSnapshotController",
        ]);
        gated.Should().Contain(g => g.RequiresFullAccess);
    }

    [Fact]
    public async Task AdministratorOnABrowserSession_ReachesEveryScopeGatedAction()
    {
        // The live regression: an Administrator (or Clinician, Caretaker, Viewer) logged into the web
        // app resolved to an empty scope set, so every one of these actions returned 403 — heart
        // rate, step count, sleep reports and client devices were unreachable for every non-owner.
        var grantedScopes = await ResolveScopesForRoleAsync(
            TenantPermissions.SeedRoles.Admin, AuthType.SessionCookie);

        grantedScopes.Should().NotBeEmpty();

        var reached = new List<string>();
        var forbidden = new List<string>();

        foreach (var gated in DiscoverGatedActions())
        {
            var result = RunAuthorizationFilters(gated, grantedScopes);
            (result is null ? reached : forbidden).Add($"{gated.Controller}.{gated.Action}");

            if (gated.RequiresFullAccess)
                result.Should().BeOfType<ForbidResult>(
                    $"{gated.Controller}.{gated.Action} requires '*', which an Administrator does not hold");
            else
                result.Should().BeNull(
                    $"{gated.Controller}.{gated.Action} is gated on a scope the Administrator role grants");
        }

        reached.Should().NotBeEmpty();

        // Nothing is refused except the "*"-gated actions — the Administrator role covers the whole
        // rest of the gated surface.
        forbidden.Should().BeEquivalentTo(DiscoverGatedActions()
            .Where(g => g.RequiresFullAccess)
            .Select(g => $"{g.Controller}.{g.Action}"));
    }

    [Fact]
    public async Task ClinicianOnABrowserSession_ReachesReadGatesAndIsRefusedWriteGates()
    {
        // The fix must not flatten the roles: a read-only member reaches the read gates and is still
        // refused the write gates on the same controller.
        var grantedScopes = await ResolveScopesForRoleAsync(
            TenantPermissions.SeedRoles.Clinician, AuthType.SessionCookie);

        RunAuthorizationFilters(GatedActionFor("SleepController", OAuthScopes.SleepRead), grantedScopes)
            .Should().BeNull("a Clinician holds sleep.read");
        RunAuthorizationFilters(GatedActionFor("SleepController", OAuthScopes.SleepReadWrite), grantedScopes)
            .Should().BeOfType<ForbidResult>("a Clinician does not hold sleep.readwrite");

        RunAuthorizationFilters(GatedActionFor("HeartRateController", OAuthScopes.HeartRateRead), grantedScopes)
            .Should().BeNull("a Clinician holds heartrate.read");
        RunAuthorizationFilters(GatedActionFor("HeartRateController", OAuthScopes.HeartRateReadWrite), grantedScopes)
            .Should().BeOfType<ForbidResult>("a Clinician does not hold heartrate.readwrite");
    }

    [Fact]
    public async Task ViewerOnABrowserSession_IsRefusedTheGatesItsRoleDoesNotCover()
    {
        // The narrowest authenticated role: glucose.read and reports.read only.
        var grantedScopes = await ResolveScopesForRoleAsync(
            TenantPermissions.SeedRoles.Viewer, AuthType.SessionCookie);

        RunAuthorizationFilters(GatedActionFor("SleepController", OAuthScopes.SleepRead), grantedScopes)
            .Should().BeOfType<ForbidResult>("a Viewer does not hold sleep.read");
        RunAuthorizationFilters(GatedActionFor("HeartRateController", OAuthScopes.HeartRateRead), grantedScopes)
            .Should().BeOfType<ForbidResult>("a Viewer does not hold heartrate.read");

        // device.notify / device.actuate are member-personal capabilities every role holds.
        RunAuthorizationFilters(
            GatedActionFor("ClientDevicesController", OAuthScopes.DeviceNotify), grantedScopes)
            .Should().BeNull("client devices are the member's own, granted to every role");
    }

    [Fact]
    public async Task DeniedMemberOnABrowserSession_IsRefusedEveryScopeGatedAction()
    {
        // Fail closed: removing the credential ceiling must not remove the membership check.
        var grantedScopes = await ResolveScopesForRoleAsync(
            TenantPermissions.SeedRoles.Denied, AuthType.SessionCookie);

        grantedScopes.Should().BeEmpty();

        foreach (var gated in DiscoverGatedActions())
        {
            RunAuthorizationFilters(gated, grantedScopes)
                .Should().BeOfType<ForbidResult>($"{gated.Controller}.{gated.Action} must stay closed");
        }
    }

    [Fact]
    public async Task AdministratorOnADelegatedToken_IsRefusedWhatTheTokenDidNotConsentTo()
    {
        // The consent boundary survives the fix: the same Administrator over a third-party token
        // scoped to glucose.read reaches nothing gated on heart rate, step count or sleep.
        var grantedScopes = await ResolveScopesForRoleAsync(
            TenantPermissions.SeedRoles.Admin, AuthType.OAuthAccessToken, OAuthScopes.GlucoseRead);

        grantedScopes.Should().BeEquivalentTo([OAuthScopes.GlucoseRead]);

        foreach (var gated in DiscoverGatedActions()
                     .Where(g => g.Controller is "HeartRateController" or "StepCountController"
                         or "ClientDevicesController" or "SleepReportController"))
        {
            RunAuthorizationFilters(gated, grantedScopes)
                .Should().BeOfType<ForbidResult>(
                    $"{gated.Controller}.{gated.Action} was never consented to");
        }
    }

    private static GatedAction GatedActionFor(string controller, string requiredScope)
    {
        return DiscoverGatedActions().First(g =>
            g.Controller == controller && RequiredScopesOf(g.Filters).Contains(requiredScope));
    }

    /// <summary>
    /// Runs the real <see cref="RequireScopeAttribute"/> filters over a context carrying the given
    /// resolved scopes, and returns the short-circuit result (<c>null</c> when the action is reached).
    /// </summary>
    private static IActionResult? RunAuthorizationFilters(
        GatedAction gated, IReadOnlySet<string> grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = Guid.CreateVersion7(),
            TenantId = TestDatabaseSeeder.TenantId,
        };
        httpContext.Items["GrantedScopes"] = grantedScopes;

        var actionContext = new ActionContext(
            httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, []);

        foreach (var filter in gated.Filters)
        {
            filter.OnAuthorization(filterContext);
            if (filterContext.Result is not null) break;
        }

        return filterContext.Result;
    }

    /// <summary>
    /// Runs the real <see cref="MemberScopeMiddleware"/> for a member holding the given seed role on
    /// the given credential type, and returns the scopes it publishes.
    /// </summary>
    private static async Task<IReadOnlySet<string>> ResolveScopesForRoleAsync(
        string roleSlug, AuthType authType, params string[] credentialScopes)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(connection).Options;

        var subjectId = Guid.CreateVersion7();
        using (var seed = new NocturneDbContext(options))
        {
            seed.Database.EnsureCreated();
            TestDatabaseSeeder.Seed(seed);

            seed.Subjects.Add(new Nocturne.Infrastructure.Data.Entities.SubjectEntity
            {
                Id = subjectId, Name = "Member", IsActive = true, IsSystemSubject = false,
            });
            var memberId = Guid.CreateVersion7();
            seed.TenantMembers.Add(new Nocturne.Infrastructure.Data.Entities.TenantMemberEntity
            {
                Id = memberId, TenantId = TestDatabaseSeeder.TenantId, SubjectId = subjectId,
            });
            var roleId = Guid.CreateVersion7();
            seed.TenantRoles.Add(new Nocturne.Infrastructure.Data.Entities.TenantRoleEntity
            {
                Id = roleId,
                TenantId = TestDatabaseSeeder.TenantId,
                Name = roleSlug,
                // TestDatabaseSeeder already seeds the canonical slugs for this tenant, and
                // (tenant_id, slug) is unique. The permission atoms are what this asserts on.
                Slug = $"{roleSlug}-under-test",
                Permissions = TenantPermissions.SeedRolePermissions[roleSlug],
                IsSystem = true,
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            });
            seed.TenantMemberRoles.Add(new Nocturne.Infrastructure.Data.Entities.TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantMemberId = memberId,
                TenantRoleId = roleId,
                SysCreatedAt = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new NocturneDbContext(options));
        using var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = authType,
            SubjectId = subjectId,
            TenantId = TestDatabaseSeeder.TenantId,
            Scopes = [.. credentialScopes],
        };
        // As AuthenticationMiddleware leaves it.
        httpContext.Items["GrantedScopes"] = OAuthScopes.Normalize(credentialScopes);
        httpContext.Items["PermissionTrie"] = new PermissionTrie();

        var middleware = new MemberScopeMiddleware(
            _ => Task.CompletedTask, NullLogger<MemberScopeMiddleware>.Instance);
        await middleware.InvokeAsync(httpContext);

        return (IReadOnlySet<string>)httpContext.Items["GrantedScopes"]!;
    }
}
