using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary><see cref="TreatmentTypeMaps"/>.</summary>
[Trait("Category", "Unit")]
public class TreatmentTypeMapsTests
{
    [Fact]
    public void Every_device_event_type_the_map_names_has_the_name_it_is_uploaded_as()
    {
        foreach (var (name, type) in TreatmentTypeMaps.DeviceEventTypeMap)
            TreatmentTypeMaps.DeviceEventTypeNames[type].Should().Be(name);

        TreatmentTypeMaps.DeviceEventTypeNames.Keys.Should().BeEquivalentTo(TreatmentTypeMaps.DeviceEventTypeMap.Values);
    }

    [Fact]
    public void A_map_with_two_names_for_one_member_cannot_be_reversed()
    {
        var map = new Dictionary<string, DeviceEventType>
        {
            ["Pump Suspend"] = DeviceEventType.PumpSuspend,
            ["Suspend"] = DeviceEventType.PumpSuspend,
        };

        var reverse = () => TreatmentTypeMaps.ReverseOf(map);

        reverse.Should().Throw<ArgumentException>();
    }
}
