using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Tests.Services.Analytics;

public class DataOverviewTablesTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void All_CoversEveryDedupRecordTypeWithAV4Table()
    {
        var expected = Enum.GetValues<RecordType>();
        expected.Should().NotBeEmpty();

        var covered = DataOverviewTables.All
            .Select(t => DuplicateGroupPrimaries.RecordTypeOf(t.GetType().GetGenericArguments()[0]))
            .OfType<RecordType>();

        covered.Should().BeEquivalentTo(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void All_KeysEveryTableUniquely()
    {
        DataOverviewTables.All.Select(t => t.CountsKey).Should().OnlyHaveUniqueItems();
    }
}
