using FluentAssertions;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Demo;

/// <summary>
/// Guards the scoping assumption that the demo account's cleanup and gating depend on.
/// </summary>
/// <remarks>
/// <c>DemoTenantService.RetireDemoMemberAsync</c> and <c>DenyDemoSubjectAttribute</c> both
/// query these tables on a context with no tenant pinned, and both are only correct because
/// the tables are not tenant-scoped and carry no RLS policy — the retire path has to see
/// memberships in <em>other</em> tenants to clean them up, and the filter has to resolve a
/// subject before any tenant is known.
/// <para>
/// If one of these becomes <see cref="ITenantScoped"/>, those queries start returning
/// nothing instead of failing: the retire path would silently stop cleaning up, and the
/// filter would silently stop refusing the demo account. Neither would show up as a test
/// failure anywhere else, so the drift is caught here instead.
/// </para>
/// </remarks>
public class DemoSubjectScopingGuardTests
{
    [Theory]
    [InlineData(typeof(SubjectEntity))]
    [InlineData(typeof(TenantMemberEntity))]
    [InlineData(typeof(RefreshTokenEntity))]
    public void TablesReadWithoutATenantContext_AreNotTenantScoped(Type entityType) =>
        typeof(ITenantScoped).IsAssignableFrom(entityType).Should().BeFalse(
            "{0} is queried without a pinned tenant by DemoTenantService.RetireDemoMemberAsync " +
            "and DenyDemoSubjectAttribute; making it ITenantScoped would make those queries " +
            "return nothing rather than fail, silently disabling demo cleanup and the demo gate. " +
            "Revisit both call sites before scoping it.",
            entityType.Name);

    /// <summary>
    /// Positive control: proves the assertion above discriminates rather than passing
    /// because <see cref="ITenantScoped"/> is never assignable from anything.
    /// </summary>
    [Fact]
    public void TheGuardDetectsATenantScopedEntity() =>
        typeof(ITenantScoped).IsAssignableFrom(typeof(AlertRuleEntity)).Should().BeTrue();
}
