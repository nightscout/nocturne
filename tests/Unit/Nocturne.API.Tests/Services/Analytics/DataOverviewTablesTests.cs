using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.Analytics;

public class DataOverviewTablesTests
{
    /// <summary>
    /// <see cref="RecordType.Entry"/> and <see cref="RecordType.Treatment"/> name the legacy tables;
    /// no repository writes either into <c>linked_records</c>, so no overview row describes them.
    /// Every other value is written by a repository's <c>DeduplicateBatchAsync</c> call and has a
    /// V4 table behind it.
    /// </summary>
    private static readonly RecordType[] LegacyRecordTypes =
    [
        RecordType.Entry,
        RecordType.Treatment,
    ];

    [Fact]
    [Trait("Category", "Unit")]
    public void All_CoversEveryDedupRecordTypeWithAV4Table()
    {
        var expected = Enum.GetValues<RecordType>().Except(LegacyRecordTypes).ToArray();
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
