using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Realtime;

[Trait("Category", "Unit")]
public class StorageDocumentEventTests
{
    private static JsonElement OnWire(object doc) =>
        (JsonElement)new StorageDocumentEvent("profile", doc).OnWire(new JsonHubProtocolOptions().PayloadSerializerOptions).Doc;

    [Fact]
    public void A_profile_document_carries_the_ranges_Loop_uploads_as_arrays()
    {
        var profile = JsonSerializer.Deserialize<Profile>(
            """
            {"loopSettings":{"preMealTargetRange":{"minValue":80,"maxValue":90},
             "overridePresets":[{"name":"Run","targetRange":{"minValue":100,"maxValue":110}},{"name":"Plain"}]}}
            """)!;

        var settings = OnWire(profile).GetProperty("loopSettings");

        settings.GetProperty("preMealTargetRange").GetRawText().Should().Be("[80,90]");
        var presets = settings.GetProperty("overridePresets");
        presets[0].GetProperty("targetRange").GetRawText().Should().Be("[100,110]");
        presets[1].TryGetProperty("targetRange", out _).Should().BeFalse();
    }
}
