using System.Reflection;
using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// <c>job_ledger</c> is a platform table with no tenant policy, and <see cref="JobCursor"/> is the
/// only thing a job writes there, so the cursor stays a position: a field added to it could carry
/// health data out of the tenant's rows.
/// </summary>
[Trait("Category", "Unit")]
public class JobCursorShapeGuardTests
{
    [Fact]
    public void A_cursor_is_a_timestamp_an_id_and_a_page_and_nothing_else()
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        typeof(JobCursor).IsSealed.Should().BeTrue("a subtype could add fields");
        typeof(JobCursor).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => (p.Name, p.PropertyType))
            .Should().BeEquivalentTo(new[]
            {
                (nameof(JobCursor.At), typeof(DateTimeOffset?)),
                (nameof(JobCursor.Id), typeof(Guid?)),
                (nameof(JobCursor.Page), typeof(int?)),
            });
        typeof(JobCursor).GetFields(instance)
            .Should().HaveCount(3, "each property's backing field, and no other");
    }
}
