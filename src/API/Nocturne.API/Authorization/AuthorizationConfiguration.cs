using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;

namespace Nocturne.API.Authorization;

/// <summary>
/// Central authorization wiring: the <see cref="PolicyNames.HasPermissions"/> policy, the
/// default-deny fallback policy, and the controller discovery filter that drops dev-only
/// controllers outside development.
/// </summary>
public static class AuthorizationConfiguration
{
    /// <summary>
    /// Registers the <see cref="PolicyNames.HasPermissions"/> policy and applies it as the
    /// <see cref="AuthorizationOptions.FallbackPolicy"/>, so every endpoint without an explicit
    /// authorization attribute requires a non-empty <see cref="Nocturne.Core.Models.PermissionTrie"/>.
    /// </summary>
    /// <remarks>
    /// The fallback admits the anonymous public subject on public tenants (its trie carries the
    /// tenant's public permissions) and rejects anonymous callers on private tenants (empty trie).
    /// Endpoints opt out with <c>[AllowAnonymous]</c>; privileged endpoints tighten access with
    /// <c>[RequireAdmin]</c> or another <c>[Require*]</c> attribute, all of which require an
    /// authenticated caller and therefore do not admit the public subject.
    /// </remarks>
    public static IServiceCollection AddNocturneAuthorization(this IServiceCollection services)
    {
        services.AddTransient<IAuthorizationHandler, HasPermissionsHandler>();

        var hasPermissionsPolicy = new AuthorizationPolicyBuilder()
            .AddRequirements(new HasPermissionsRequirement())
            .Build();

        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyNames.HasPermissions, hasPermissionsPolicy);
            options.FallbackPolicy = hasPermissionsPolicy;

            // DefaultPolicy is deliberately left at the framework's RequireAuthenticatedUser.
            // Adding HasPermissionsRequirement to it looks like a tightening but is unsatisfiable
            // on the tenantless surface: those paths resolve no TenantContext, so
            // MemberScopeMiddleware returns before rebuilding the trie, leaving whatever
            // AuthenticationMiddleware built from the subject's *global* roles. A member who
            // joined by invite has none, so the trie is empty and every bare-[Authorize]
            // tenantless endpoint 403s — including the apex tenant list, the cross-tenant
            // overview, and first-tenant creation.
            //
            // This leaves a gap that is OPEN, not covered elsewhere: a member of the resolved
            // tenant holding a zero-permission role (the Denied seed role) has an empty trie and
            // empty scopes, and can still read any bare-[Authorize] endpoint. Most of
            // Controllers/V4 carries no scope attribute today, so per-action gating does not yet
            // close it. Closing it belongs with those attributes rather than here, because a
            // DefaultPolicy strong enough to catch it also breaks the tenantless surface above.
        });

        return services;
    }

    /// <summary>
    /// Configures controller discovery so controllers in the <c>.DevOnly</c> namespace are not
    /// registered outside the development environment.
    /// </summary>
    /// <remarks>
    /// Replaces the default <see cref="ControllerFeatureProvider"/> rather than appending a second
    /// provider: feature providers only add to <see cref="ControllerFeature.Controllers"/>, so an
    /// appended filter cannot remove controllers the default provider has already discovered.
    /// </remarks>
    public static void ConfigureControllerDiscovery(ApplicationPartManager manager, bool isDevelopment)
    {
        if (isDevelopment)
        {
            return;
        }

        var defaultProvider = manager.FeatureProviders
            .FirstOrDefault(p => p.GetType() == typeof(ControllerFeatureProvider));
        if (defaultProvider is not null)
        {
            manager.FeatureProviders.Remove(defaultProvider);
        }

        manager.FeatureProviders.Add(new DevOnlyExcludingControllerFeatureProvider());
    }
}

/// <summary>
/// Controller feature provider that excludes controllers in the <c>.DevOnly</c> namespace.
/// </summary>
public sealed class DevOnlyExcludingControllerFeatureProvider : ControllerFeatureProvider
{
    /// <inheritdoc />
    protected override bool IsController(TypeInfo typeInfo)
    {
        if (typeInfo.Namespace?.Contains(".DevOnly", StringComparison.Ordinal) == true)
        {
            return false;
        }

        return base.IsController(typeInfo);
    }
}
