using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary>
/// Pins each <see cref="RecordType"/> to the key already stored in <c>linked_records.record_type</c>,
/// so renaming a member orphans no rows and adding one cannot skip the pin.
/// </summary>
[Trait("Category", "Unit")]
public class RecordTypeKeysTests
{
    private static readonly Dictionary<RecordType, string> StoredKeys = new()
    {
        [RecordType.StateSpan] = "statespan",
        [RecordType.SensorGlucose] = "sensorglucose",
        [RecordType.Bolus] = "bolus",
        [RecordType.CarbIntake] = "carbintake",
        [RecordType.BGCheck] = "bgcheck",
        [RecordType.DeviceEvent] = "deviceevent",
        [RecordType.Note] = "note",
        [RecordType.BolusCalculation] = "boluscalculation",
        [RecordType.TempBasal] = "tempbasal",
    };

    public static TheoryData<RecordType, string> Pins()
    {
        var data = new TheoryData<RecordType, string>();
        foreach (var (recordType, stored) in StoredKeys)
        {
            data.Add(recordType, stored);
        }

        return data;
    }

    [Fact]
    public void Every_record_type_is_pinned()
    {
        StoredKeys.Keys.Should().BeEquivalentTo(Enum.GetValues<RecordType>());
    }

    [Theory]
    [MemberData(nameof(Pins))]
    public void Key_is_the_stored_key(RecordType recordType, string stored)
    {
        RecordTypeKeys.Key(recordType).Should().Be(stored);
    }
}
