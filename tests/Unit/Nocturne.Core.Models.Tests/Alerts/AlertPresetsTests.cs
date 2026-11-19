using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.Core.Models.Tests.Alerts;

public class AlertPresetsTests
{
    [Fact]
    public void EveryPresetHasItsOwnId()
    {
        AlertPresets.All.Select(p => p.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryStarterKindIsOnePreset()
    {
        foreach (var kind in Enum.GetValues<StarterAlertKind>())
            AlertPresets.Starter(kind).StarterKind.Should().Be(kind);
    }

    [Fact]
    public void TheStartersAreInTheOrderOfTheirKinds()
    {
        AlertPresets.All.Where(p => p.StarterKind is not null).Select(p => p.StarterKind)
            .Should().BeInAscendingOrder();
    }

    [Fact]
    public void APresetHasAConditionOrReadsOnlyATracker()
    {
        foreach (var preset in AlertPresets.All)
        {
            if (preset.ConditionType is null)
            {
                preset.ConditionParams.Should().BeNull(preset.Id);
                preset.TrackerCategories.Should().NotBeEmpty(preset.Id);
                preset.TrackerOnlyAgeHours.Should().BePositive(preset.Id);
                continue;
            }

            preset.TrackerOnlyAgeHours.Should().BeNull(preset.Id);
            using var json = JsonDocument.Parse(preset.ConditionParams!);
            json.RootElement.ValueKind.Should().Be(JsonValueKind.Object, preset.Id);
        }
    }

    [Fact]
    public void AStarterThatIsAGlucoseLevelClosesOnTheOtherSideOfIt()
    {
        AlertPresets.All
            .Where(p => p.StarterKind is not null && p.ConditionType == AlertConditionType.Threshold)
            .Should().OnlyContain(p => p.AutoResolve);
    }

    [Theory]
    [InlineData(AlertConditionType.Predicted)]
    [InlineData(AlertConditionType.RateOfChange)]
    [InlineData(AlertConditionType.SignalLoss)]
    [InlineData(AlertConditionType.LoopStale)]
    public void AConditionWithNoOppositeDoesNotCloseOnOne(AlertConditionType type)
    {
        AlertPresets.All.Where(p => p.ConditionType == type)
            .Should().NotBeEmpty().And.OnlyContain(p => !p.AutoResolve);
    }
}
