using Moq;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Tests.Shared.Connectors;

/// <summary>The tenant context a connector's token provider needs: resolved, with a fresh tenant id.</summary>
public static class ResolvedTenant
{
    public static ITenantAccessor Accessor()
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.Setup(t => t.IsResolved).Returns(true);
        tenant.Setup(t => t.TenantId).Returns(Guid.NewGuid());
        return tenant.Object;
    }
}
