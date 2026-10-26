using System.Reflection;
using FluentAssertions;
using Nocturne.API.Controllers.V4.Monitoring;
using OpenApi.Remote.Attributes;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// A write refreshes every tracker read whose rows it changes, so the page needs no second
/// round trip to catch up.
/// </summary>
[Trait("Category", "Unit")]
public class TrackerInvalidationTests
{
    /// <summary>
    /// Instances and presets reference their definition with a cascading foreign key, so deleting
    /// a definition empties them too.
    /// </summary>
    [Fact]
    public void DeletingADefinition_RefreshesTheInstancesAndPresetsItTakesWithIt()
    {
        Command(nameof(TrackersController.DeleteDefinition)).Invalidates.Should().Contain(
        [
            nameof(TrackersController.GetDefinitions),
            nameof(TrackersController.GetActiveInstances),
            nameof(TrackersController.GetInstanceHistory),
            nameof(TrackersController.GetPresets),
        ]);
    }

    /// <summary>A start replaces the running instance, which moves into history.</summary>
    [Theory]
    [InlineData(nameof(TrackersController.StartInstance))]
    [InlineData(nameof(TrackersController.ApplyPreset))]
    public void Starting_RefreshesTheActiveInstancesAndTheHistory(string write)
    {
        Command(write).Invalidates.Should().Contain(
        [
            nameof(TrackersController.GetActiveInstances),
            nameof(TrackersController.GetInstanceHistory),
        ]);
    }

    private static RemoteCommandAttribute Command(string write) =>
        typeof(TrackersController).GetMethod(write)!
            .GetCustomAttribute<RemoteCommandAttribute>()!;
}
