using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Services;

/// <summary>
/// <see cref="AuditRowIds"/> on PostgreSQL: the rows of one save share <c>created_at</c>, so reading
/// them back ordered by id must give the order they were staged in.
/// </summary>
[Trait("Category", "Integration")]
public class AuditRowIdsPostgresTests(Repositories.TenantMembershipRepositoryPostgresTests.Database database)
    : IClassFixture<Repositories.TenantMembershipRepositoryPostgresTests.Database>
{
    private const int RowsPerSave = 50;

    private NocturneDbContext Context() =>
        TestContexts.Context(database.AppConnectionString);

    [Fact]
    public async Task RowsStagedInOneSaveReadBackByIdInTheOrderTheyWereStaged()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var target = Guid.NewGuid();
            await using (var db = Context())
            {
                for (var i = 0; i < RowsPerSave; i++)
                {
                    AuthAuditRows.Stage(db, AuthAuditEventType.RoleAssigned, tenantId: null, teamId: null, target,
                        new Dictionary<string, object?> { ["n"] = i });
                }

                await db.SaveChangesAsync();
            }

            await using var read = Context();
            var rows = await read.AuthAuditLog.AsNoTracking()
                .Where(a => a.TargetSubjectId == target)
                .OrderBy(a => a.Id)
                .Select(a => new { a.DetailsJson, a.CreatedAt })
                .ToListAsync();

            rows.Select(r => r.CreatedAt).Distinct().Should().ContainSingle("the rows of one save share created_at");
            rows.Select(r => r.DetailsJson).Should().Equal(
                Enumerable.Range(0, RowsPerSave).Select(i => $$"""{"n": {{i}}}"""));
        }
    }

    [Fact]
    public void ThePostgresOrderIsTheBigEndianByteOrder()
    {
        var low = new Guid("00000000-0000-7000-8000-0000000000ff");
        var high = new Guid("00000000-0000-7000-8000-000000000100");

        AuditRowIds.Compare(low, high).Should().BeNegative();
        AuditRowIds.Compare(new Guid("01000000-0000-7000-8000-000000000000"), new Guid("00ffffff-ffff-7fff-bfff-ffffffffffff"))
            .Should().BePositive();
    }
}
