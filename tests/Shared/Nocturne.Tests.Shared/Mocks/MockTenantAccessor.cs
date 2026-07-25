using Moq;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Tests.Shared.Mocks;

/// <summary>
/// Shared factory for creating a pre-configured ITenantAccessor mock
/// with a standard test tenant. Eliminates the 5-line mock setup block
/// that was duplicated across 10+ test files.
/// </summary>
public static class MockTenantAccessor
{
    public static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public const string DefaultSlug = "test-tenant";
    public const string DefaultDisplayName = "Test Tenant";

    /// <summary>
    /// Creates a fully configured ITenantAccessor mock with default test tenant values.
    /// </summary>
    public static Mock<ITenantAccessor> Create(
        Guid? tenantId = null,
        string? slug = null,
        string? displayName = null,
        bool isActive = true,
        bool isDemo = false)
    {
        var id = tenantId ?? DefaultTenantId;
        var context = new TenantContext(
            id, slug ?? DefaultSlug, displayName ?? DefaultDisplayName, isActive, isDemo);

        var mock = new Mock<ITenantAccessor>();
        mock.Setup(x => x.Context).Returns(context);
        mock.Setup(x => x.IsResolved).Returns(true);
        mock.Setup(x => x.TenantId).Returns(id);
        return mock;
    }
}
