using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.Analytics;

public class DataOverviewTablesTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void All_CoversEveryDedupRecordTypeWithAV4Table()
    {
        var expected = Enum.GetValues<RecordType>();
        expected.Should().NotBeEmpty();

        var covered = DataOverviewTables
            .All.Where(t => t.DedupRecordType.HasValue)
            .Select(t => t.DedupRecordType!.Value);

        covered.Should().BeEquivalentTo(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void All_KeysEveryTableUniquely()
    {
        DataOverviewTables.All.Select(t => t.CountsKey).Should().OnlyHaveUniqueItems();
    }
}
