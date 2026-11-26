using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Tests.Shared.Connectors;

/// <summary>The tenant context a connector's token provider needs: resolved, with a fresh tenant id.</summary>
public static class ResolvedTenant
{
    public static ITenantAccessor Accessor() => SettableTenantAccessor.Resolved(Guid.NewGuid());
}
