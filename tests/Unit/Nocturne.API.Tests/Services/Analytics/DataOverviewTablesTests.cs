using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;

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

    [Fact]
    [Trait("Category", "Unit")]
    public void All_ListsItsTablesInTheOrderTheCountsAreSerialised()
    {
        DataOverviewTables.All.Select(t => t.CountsKey).Should().Equal(
            "Glucose", "ManualBG", "Boluses", "CarbIntake", "BolusCalculations", "Notes", "DeviceEvents",
            "StateSpans", "DeviceStatus", "BGChecks", "TempBasals", "BasalInjections");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void All_AttributesEveryTableButTheApsSnapshotsToADataSource()
    {
        using var context = TestDbContextFactory.CreateInMemoryContext($"overview_{Guid.NewGuid()}");

        DataOverviewTables.All.Where(t => t.Sources(context) is null).Select(t => t.CountsKey)
            .Should().Equal("DeviceStatus");
    }
}
