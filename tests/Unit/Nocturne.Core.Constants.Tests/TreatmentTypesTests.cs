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

    [Theory]
    [InlineData(TreatmentTypes.TemporaryOverride, "Temporary Override")]
    [InlineData(TreatmentTypes.TemporaryOverrideCancel, "Temporary Override Cancel")]
    [InlineData(TreatmentTypes.TemporaryTarget, "Temporary Target")]
    [InlineData(TreatmentTypes.TemporaryTargetCancel, "Temporary Target Cancel")]
    [InlineData(TreatmentTypes.Note, "Note")]
    [InlineData(TreatmentTypes.Announcement, "Announcement")]
    [InlineData(TreatmentTypes.Exercise, "Exercise")]
    [InlineData(TreatmentTypes.ExternalInsulin, "External Insulin")]
    [InlineData(TreatmentTypes.Carbs, "Carbs")]
    [InlineData(TreatmentTypes.OpenApsOffline, "OpenAPS Offline")]
    public void EventTypeConstant_IsTheWireValueNightscoutUses(string constant, string wireValue)
    {
        constant.Should().Be(wireValue);
    }
}
