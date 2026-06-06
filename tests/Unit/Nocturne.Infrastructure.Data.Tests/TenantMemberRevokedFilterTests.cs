using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Verifies the single enforcement point for "active membership": the global query filter on
/// <see cref="TenantMemberEntity"/> (<c>RevokedAt == null</c>). Every membership check across the
/// auth gates relies on this filter rather than repeating the predicate, so a revoked membership
/// must be invisible to ordinary queries and only reachable via <c>IgnoreQueryFilters()</c>.
/// EF InMemory honours global query filters, matching the pattern used by the alert-scoping tests.
/// </summary>
public class TenantMemberRevokedFilterTests
{
    [Fact]
    public async Task TenantMembers_AreExcludedWhenRevoked()
    {
        var options = NewStore();
        var tenantId = Guid.NewGuid();
        var activeSubject = Guid.NewGuid();
        var revokedSubject = Guid.NewGuid();

        await using (var seedCtx = new NocturneDbContext(options))
        {
            seedCtx.TenantMembers.AddRange(
                NewMember(tenantId, activeSubject, revokedAt: null),
                NewMember(tenantId, revokedSubject, revokedAt: DateTime.UtcNow));
            await seedCtx.SaveChangesAsync();
        }

        await using var ctx = new NocturneDbContext(options);

        var visible = await ctx.TenantMembers.Select(m => m.SubjectId).ToListAsync();
        visible.Should().BeEquivalentTo(new[] { activeSubject },
            "the global query filter must hide revoked memberships from every membership query");

        var all = await ctx.TenantMembers.IgnoreQueryFilters().Select(m => m.SubjectId).ToListAsync();
        all.Should().BeEquivalentTo(new[] { activeSubject, revokedSubject },
            "IgnoreQueryFilters bypasses the filter, confirming the revoked row exists but is filtered");
    }

    private static DbContextOptions<NocturneDbContext> NewStore() =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"tenant_member_revoked_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static TenantMemberEntity NewMember(Guid tenantId, Guid subjectId, DateTime? revokedAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        SubjectId = subjectId,
        RevokedAt = revokedAt,
    };
}
