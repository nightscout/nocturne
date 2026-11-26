using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// An <see cref="ITenantAccessor"/> that starts unresolved, as a scope opened in code does, or at
/// <paramref name="context"/>; <see cref="SetTenant"/> replaces it.
/// </summary>
/// <param name="context">The tenant the accessor starts resolved to, or null to start unresolved.</param>
/// <param name="onSet">Called with each context <see cref="SetTenant"/> receives, for a test that asserts which scopes were pinned.</param>
public sealed class SettableTenantAccessor(TenantContext? context = null, Action<TenantContext>? onSet = null) : ITenantAccessor
{
    public static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public const string DefaultSlug = "test-tenant";
    public const string DefaultDisplayName = "Test Tenant";

    public Guid TenantId => Context?.TenantId ?? Guid.Empty;
    public bool IsResolved => Context is not null;
    public TenantContext? Context { get; private set; } = context;

    /// <summary>An accessor resolved to a test tenant: <see cref="DefaultTenantId"/> unless <paramref name="tenantId"/> names another.</summary>
    public static SettableTenantAccessor Resolved(
        Guid? tenantId = null,
        string? slug = null,
        string? displayName = null,
        bool isActive = true,
        bool isDemo = false) =>
        new(new TenantContext(
            tenantId ?? DefaultTenantId, slug ?? DefaultSlug, displayName ?? DefaultDisplayName, isActive, isDemo));

    public void SetTenant(TenantContext context)
    {
        Context = context;
        onSet?.Invoke(context);
    }
}
