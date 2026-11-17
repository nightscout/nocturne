using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services.WriteBack;

/// <summary>
/// A profile written back must leave the upstream Nightscout the way Loop would have uploaded it:
/// target ranges as <c>[min, max]</c> arrays, which NightscoutKit and LoopFollow decode as <c>[Double]</c>.
/// </summary>
[Trait("Category", "Unit")]
public class NightscoutProfileWriteBackSinkTests
{
    private readonly NightscoutConnectorConfiguration _config = new()
    {
        Url = "https://nightscout.example.com",
        ApiSecret = "test-secret-12345",
        WriteBackEnabled = true,
        WriteBackBatchSize = 50,
    };

    private NightscoutProfileWriteBackSink Sink(RecordingHttpMessageHandler handler)
    {
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_config);
        return new NightscoutProfileWriteBackSink(
            new HttpClient(handler) { BaseAddress = new Uri(_config.Url) },
            loader.Object,
            new NightscoutCircuitBreaker(new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture))),
            NullLogger<NightscoutProfileWriteBackSink>.Instance);
    }

    private static Profile LoopProfile() => new()
    {
        DefaultProfile = "Default",
        LoopSettings = new LoopProfileSettings
        {
            PreMealTargetRange = new LoopTargetRange { MinValue = 80, MaxValue = 90 },
            WorkoutTargetRange = new LoopTargetRange { MinValue = 140, MaxValue = 160 },
            OverridePresets =
            [
                new LoopOverridePreset { Name = "Run", TargetRange = new LoopTargetRange { MinValue = 100, MaxValue = 110 } },
                new LoopOverridePreset { Name = "Half", TargetRange = new LoopTargetRange { MinValue = 100 } },
                new LoopOverridePreset { Name = "Plain" },
            ],
        },
    };

    private static async Task<JsonObject> SentSettingsAsync(Func<NightscoutProfileWriteBackSink, Task> write)
    {
        var handler = new RecordingHttpMessageHandler();
        var sink = new NightscoutProfileWriteBackSinkTests().Sink(handler);

        await write(sink);

        handler.RequestCount.Should().Be(1);
        var body = JsonNode.Parse(handler.Bodies[0])!;
        var document = body is JsonArray array ? array[0]! : body;
        return document["loopSettings"]!.AsObject();
    }

    private static void AssertArrays(JsonObject settings)
    {
        settings["preMealTargetRange"]!.ToJsonString().Should().Be("[80,90]");
        var presets = settings["overridePresets"]!.AsArray();
        presets[0]!["targetRange"]!.ToJsonString().Should().Be("[100,110]");
        presets[1]!["targetRange"].Should().BeNull();
        presets[2]!["targetRange"].Should().BeNull();
        settings["workoutTargetRange"]!["minValue"]!.GetValue<double>().Should().Be(140);
    }

    [Fact]
    public async Task A_created_profile_is_sent_with_its_ranges_as_arrays() =>
        AssertArrays(await SentSettingsAsync(sink => sink.OnCreatedAsync(LoopProfile())));

    [Fact]
    public async Task An_updated_profile_is_sent_with_its_ranges_as_arrays() =>
        AssertArrays(await SentSettingsAsync(sink => sink.OnUpdatedAsync(LoopProfile())));
}
