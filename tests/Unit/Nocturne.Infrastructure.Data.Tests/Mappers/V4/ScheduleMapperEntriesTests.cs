using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

/// <summary>
/// The four schedule mappers each project one <c>entries_json</c> column into a non-nullable
/// list, so they share one contract: an unreadable column reads as an empty schedule.
/// </summary>
[Trait("Category", "Unit")]
public class ScheduleMapperEntriesTests
{
    public static TheoryData<string, Func<string?, int>> EntryCounts => new()
    {
        {
            nameof(BasalScheduleMapper),
            json => BasalScheduleMapper.ToDomainModel(
                new BasalScheduleEntity { EntriesJson = json! }).Entries.Count
        },
        {
            nameof(CarbRatioScheduleMapper),
            json => CarbRatioScheduleMapper.ToDomainModel(
                new CarbRatioScheduleEntity { EntriesJson = json! }).Entries.Count
        },
        {
            nameof(SensitivityScheduleMapper),
            json => SensitivityScheduleMapper.ToDomainModel(
                new SensitivityScheduleEntity { EntriesJson = json! }).Entries.Count
        },
        {
            nameof(TargetRangeScheduleMapper),
            json => TargetRangeScheduleMapper.ToDomainModel(
                new TargetRangeScheduleEntity { EntriesJson = json! }).Entries.Count
        },
    };

    [Theory]
    [MemberData(nameof(EntryCounts))]
    public void ToDomainModel_UnreadableEntriesJson_YieldsNoEntries(
        string mapper, Func<string?, int> entryCount)
    {
        entryCount("""[{"start":""").Should().Be(0,
            "{0}: one unparseable jsonb row must not fail the read of every record around it",
            mapper);
        entryCount(null).Should().Be(0, "{0}: a null column is not a parse failure either", mapper);
    }
}
