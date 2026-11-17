using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Security;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Entities;

[Trait("Category", "Unit")]
public class AuthAuditLogEntityTests
{
    /// <summary>
    /// A tenant query filter and RLS policy would leave the rows written before a tenant resolves,
    /// failed logins among them, unwritable and unreadable; see the remarks on
    /// <see cref="AuthAuditLogEntity"/>.
    /// </summary>
    [Fact]
    public void TheAuthAuditLogIsNotTenantScoped()
    {
        using var context = OfflineDbContext.Create();

        typeof(ITenantScoped).IsAssignableFrom(typeof(AuthAuditLogEntity)).Should().BeFalse();
        ShareRlsPolicy.TenantScopedTableNames(context.Model).Should().NotContain("auth_audit_log");
    }
}
