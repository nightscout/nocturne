using FluentAssertions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

public class GlucoseProcessingResolverTests
{
    private readonly GlucoseProcessingResolver _resolver;
    private readonly Mock<IGlucoseProcessingConfigProvider> _configProvider;

    public GlucoseProcessingResolverTests()
    {
        _configProvider = new Mock<IGlucoseProcessingConfigProvider>();
        _configProvider.Setup(x => x.GetPreferredProcessingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((GlucoseProcessing?)null);
        _configProvider.Setup(x => x.GetSourceDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GlucoseProcessingSourceDefault>());
        _resolver = new GlucoseProcessingResolver(_configProvider.Object);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_MgdlWithSmoothedProcessing_SlotsIntoSmoothedMgdl()
    {
        var model = new SensorGlucose { Mgdl = 120 };
        await _resolver.ResolveAsync(model, "Smoothed", null, null);
        model.GlucoseProcessing.Should().Be(GlucoseProcessing.Smoothed);
        model.SmoothedMgdl.Should().Be(120);
        model.UnsmoothedMgdl.Should().BeNull();
        model.Mgdl.Should().Be(120);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_MgdlWithUnsmoothedProcessing_SlotsIntoUnsmoothedMgdl()
    {
        var model = new SensorGlucose { Mgdl = 125 };
        await _resolver.ResolveAsync(model, "Unsmoothed", null, null);
        model.GlucoseProcessing.Should().Be(GlucoseProcessing.Unsmoothed);
        model.SmoothedMgdl.Should().BeNull();
        model.UnsmoothedMgdl.Should().Be(125);
        model.Mgdl.Should().Be(125);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_NoProcessingInfo_LeavesFieldsNull()
    {
        var model = new SensorGlucose { Mgdl = 120 };
        await _resolver.ResolveAsync(model, null, null, null);
        model.GlucoseProcessing.Should().BeNull();
        model.SmoothedMgdl.Should().BeNull();
        model.UnsmoothedMgdl.Should().BeNull();
        model.Mgdl.Should().Be(120);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_BothTypedFields_PreferenceSmoothed_SetsMgdlToSmoothed()
    {
        _configProvider.Setup(x => x.GetPreferredProcessingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(GlucoseProcessing.Smoothed);
        var model = new SensorGlucose { Mgdl = 0 };
        await _resolver.ResolveAsync(model, null, 118, 125);
        model.SmoothedMgdl.Should().Be(118);
        model.UnsmoothedMgdl.Should().Be(125);
        model.Mgdl.Should().Be(118);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_BothTypedFields_PreferenceUnsmoothed_SetsMgdlToUnsmoothed()
    {
        _configProvider.Setup(x => x.GetPreferredProcessingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(GlucoseProcessing.Unsmoothed);
        var model = new SensorGlucose { Mgdl = 0 };
        await _resolver.ResolveAsync(model, null, 118, 125);
        model.Mgdl.Should().Be(125);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_BothTypedFields_NoPreference_UsePayloadMgdl()
    {
        var model = new SensorGlucose { Mgdl = 120 };
        await _resolver.ResolveAsync(model, null, 118, 125);
        model.Mgdl.Should().Be(120);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_BothTypedFields_NoPreference_NoPayloadMgdl_FallsToSmoothed()
    {
        var model = new SensorGlucose { Mgdl = 0 };
        await _resolver.ResolveAsync(model, null, 118, 125);
        model.Mgdl.Should().Be(118);
    }

    // Row 7: Only smoothedMgdl typed field, preference unsmoothed — fallback to smoothed
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_OnlySmoothedTypedField_PreferenceUnsmoothed_FallsBackToSmoothed()
    {
        _configProvider.Setup(x => x.GetPreferredProcessingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(GlucoseProcessing.Unsmoothed);
        var model = new SensorGlucose { Mgdl = 0 };
        await _resolver.ResolveAsync(model, null, 118, null);
        model.SmoothedMgdl.Should().Be(118);
        model.UnsmoothedMgdl.Should().BeNull();
        model.Mgdl.Should().Be(118); // fallback — nothing to swap to
    }

    // Mirror: Only unsmoothedMgdl typed field, preference smoothed — fallback to unsmoothed
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_OnlyUnsmoothedTypedField_PreferenceSmoothed_FallsBackToUnsmoothed()
    {
        _configProvider.Setup(x => x.GetPreferredProcessingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(GlucoseProcessing.Smoothed);
        var model = new SensorGlucose { Mgdl = 0 };
        await _resolver.ResolveAsync(model, null, null, 125);
        model.SmoothedMgdl.Should().BeNull();
        model.UnsmoothedMgdl.Should().Be(125);
        model.Mgdl.Should().Be(125); // fallback — nothing to swap to
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_SourceDefault_MatchesByDeviceStartsWith()
    {
        _configProvider.Setup(x => x.GetSourceDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GlucoseProcessingSourceDefault>
            {
                new() { Match = "xDrip", Field = "device", Processing = GlucoseProcessing.Smoothed }
            });
        var model = new SensorGlucose { Mgdl = 120, Device = "xDrip-DexcomG5" };
        await _resolver.ResolveAsync(model, null, null, null);
        model.GlucoseProcessing.Should().Be(GlucoseProcessing.Smoothed);
        model.SmoothedMgdl.Should().Be(120);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_SourceDefault_MatchesByApp()
    {
        _configProvider.Setup(x => x.GetSourceDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GlucoseProcessingSourceDefault>
            {
                new() { Match = "Juggluco", Field = "app", Processing = GlucoseProcessing.Unsmoothed }
            });
        var model = new SensorGlucose { Mgdl = 125, App = "Juggluco" };
        await _resolver.ResolveAsync(model, null, null, null);
        model.GlucoseProcessing.Should().Be(GlucoseProcessing.Unsmoothed);
        model.UnsmoothedMgdl.Should().Be(125);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_PayloadOverridesSourceDefault()
    {
        _configProvider.Setup(x => x.GetSourceDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GlucoseProcessingSourceDefault>
            {
                new() { Match = "xDrip", Field = "device", Processing = GlucoseProcessing.Smoothed }
            });
        var model = new SensorGlucose { Mgdl = 125, Device = "xDrip-DexcomG5" };
        await _resolver.ResolveAsync(model, "Unsmoothed", null, null);
        model.GlucoseProcessing.Should().Be(GlucoseProcessing.Unsmoothed);
        model.UnsmoothedMgdl.Should().Be(125);
        model.SmoothedMgdl.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_BothTypedFieldsWithMgdl_TypedFieldsTakePrecedence()
    {
        _configProvider.Setup(x => x.GetPreferredProcessingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(GlucoseProcessing.Smoothed);
        var model = new SensorGlucose { Mgdl = 120 };
        await _resolver.ResolveAsync(model, null, 118, 125);
        model.SmoothedMgdl.Should().Be(118);
        model.UnsmoothedMgdl.Should().Be(125);
        model.Mgdl.Should().Be(118); // preference picks smoothed, not payload 120
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_SourceDefault_NoMatch_RemainsUnknown()
    {
        _configProvider.Setup(x => x.GetSourceDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GlucoseProcessingSourceDefault>
            {
                new() { Match = "xDrip", Field = "device", Processing = GlucoseProcessing.Smoothed }
            });
        var model = new SensorGlucose { Mgdl = 120, Device = "AAPS" };
        await _resolver.ResolveAsync(model, null, null, null);
        model.GlucoseProcessing.Should().BeNull();
        model.SmoothedMgdl.Should().BeNull();
        model.UnsmoothedMgdl.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_SourceDefault_FirstMatchWins()
    {
        _configProvider.Setup(x => x.GetSourceDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GlucoseProcessingSourceDefault>
            {
                new() { Match = "xDrip", Field = "device", Processing = GlucoseProcessing.Unsmoothed },
                new() { Match = "xDrip", Field = "device", Processing = GlucoseProcessing.Smoothed },
            });
        var model = new SensorGlucose { Mgdl = 120, Device = "xDrip-DexcomG5" };
        await _resolver.ResolveAsync(model, null, null, null);
        model.GlucoseProcessing.Should().Be(GlucoseProcessing.Unsmoothed);
    }

    // Invalid glucoseProcessing string falls through to source defaults
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_InvalidGlucoseProcessingString_FallsToSourceDefaults()
    {
        _configProvider.Setup(x => x.GetSourceDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GlucoseProcessingSourceDefault>
            {
                new() { Match = "xDrip", Field = "device", Processing = GlucoseProcessing.Smoothed }
            });
        var model = new SensorGlucose { Mgdl = 120, Device = "xDrip-DexcomG5" };
        await _resolver.ResolveAsync(model, "garbage", null, null);
        // Invalid string is ignored, falls through to source defaults
        model.GlucoseProcessing.Should().Be(GlucoseProcessing.Smoothed);
        model.SmoothedMgdl.Should().Be(120);
    }

    // Case-insensitive glucoseProcessing parsing
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_CaseInsensitiveGlucoseProcessing()
    {
        var model = new SensorGlucose { Mgdl = 120 };
        await _resolver.ResolveAsync(model, "smoothed", null, null);
        model.GlucoseProcessing.Should().Be(GlucoseProcessing.Smoothed);
        model.SmoothedMgdl.Should().Be(120);
    }

    // Both typed fields, preference unsmoothed — assert ALL fields
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolve_BothTypedFields_PreferenceUnsmoothed_AllFieldsCorrect()
    {
        _configProvider.Setup(x => x.GetPreferredProcessingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(GlucoseProcessing.Unsmoothed);
        var model = new SensorGlucose { Mgdl = 0 };
        await _resolver.ResolveAsync(model, null, 118, 125);
        model.SmoothedMgdl.Should().Be(118);
        model.UnsmoothedMgdl.Should().Be(125);
        model.Mgdl.Should().Be(125);
    }
}
