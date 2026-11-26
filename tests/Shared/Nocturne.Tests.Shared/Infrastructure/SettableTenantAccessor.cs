using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>An <see cref="ITenantAccessor"/> that starts unresolved, as a scope opened in code does.</summary>
public sealed class SettableTenantAccessor : ITenantAccessor
{
    public Guid TenantId => Context?.TenantId ?? Guid.Empty;
    public bool IsResolved => Context is not null;
    public TenantContext? Context { get; private set; }
    public void SetTenant(TenantContext context) => Context = context;
}
