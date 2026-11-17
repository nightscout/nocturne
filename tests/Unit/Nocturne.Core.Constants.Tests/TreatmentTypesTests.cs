using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Constants.Tests;

public class TreatmentTypesTests
{
    [Fact]
    public void TempBasalEventTypes_AreTheThreeSpellingsUploadersSend()
    {
        TreatmentTypes.TempBasalEventTypes.Should().BeEquivalentTo(
            "Temp Basal", "Temp Basal Start", "TempBasal");
    }

    [Fact]
    public void InsulinContextBolusEventTypes_AreFourBolusTypes()
    {
        TreatmentTypes.InsulinContextBolusEventTypes.Should().BeEquivalentTo(
            "Snack Bolus", "Meal Bolus", "Correction Bolus", "Combo Bolus");
    }

    [Fact]
    public void InsulinContextBasalEventTypes_LeaveOutTheOneWordSpelling()
    {
        TreatmentTypes.InsulinContextBasalEventTypes.Should().BeEquivalentTo(
            "Temp Basal", "Temp Basal Start");
        TreatmentTypes.TempBasalEventTypes.Should().Contain(TreatmentTypes.InsulinContextBasalEventTypes);
        TreatmentTypes.TempBasalEventTypes.Should().Contain("TempBasal");
    }
}
