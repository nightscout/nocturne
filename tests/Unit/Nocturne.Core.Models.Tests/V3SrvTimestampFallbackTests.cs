using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary>
/// AAPS's NS v3 socket handler reads <c>doc.srvModified</c> with <c>getLong</c> before
/// dispatching on the collection, so a realtime storage event whose doc lacks a numeric
/// <c>srvModified</c> throws and is dropped client-side (#638). Every model broadcast as a
/// storage event must therefore serialize <c>srvModified</c>/<c>srvCreated</c>, falling
/// back to Mills — the same event-time timeline the V3 REST layer projects, so a
/// socket-derived high-water mark stays coherent with REST catch-up loads.
/// </summary>
[Trait("Category", "Unit")]
public class V3SrvTimestampFallbackTests
{
    private const long Mills = 1_722_945_600_000;

    public static TheoryData<string, object> BroadcastDocsWithMills =>
        new()
        {
            { "entries", new Entry { Mills = Mills } },
            { "treatments", new Treatment { Mills = Mills } },
            { "devicestatus", new DeviceStatus { Mills = Mills } },
            { "profile", new Profile { Mills = Mills } },
        };

    [Theory]
    [MemberData(nameof(BroadcastDocsWithMills))]
    public void BroadcastDoc_WithMills_SerializesSrvTimestampsAsMills(
        string collection,
        object doc
    )
    {
        var root = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(doc));

        root.GetProperty("srvModified")
            .GetInt64()
            .Should()
            .Be(Mills, $"AAPS drops {collection} docs without a numeric srvModified");
        root.GetProperty("srvCreated").GetInt64().Should().Be(Mills);
    }

    [Fact]
    public void ExplicitSrvTimestamps_WinOverTheMillsFallback()
    {
        var entry = new Entry
        {
            Mills = Mills,
            SrvModified = Mills + 5_000,
            SrvCreated = Mills + 1_000,
        };

        entry.SrvModified.Should().Be(Mills + 5_000);
        entry.SrvCreated.Should().Be(Mills + 1_000);
    }

    [Fact]
    public void Entry_WithoutMills_DoesNotFabricateSrvTimestamps()
    {
        var entry = new Entry { Mills = 0 };

        entry.SrvModified.Should().BeNull();
        entry.SrvCreated.Should().BeNull();
    }

    [Fact]
    public void Profile_WithoutMills_FallsBackToStartDate()
    {
        var profile = new Profile { Mills = 0, StartDate = "2026-08-06T12:00:00.000Z" };
        var expected = DateTimeOffset.Parse("2026-08-06T12:00:00.000Z").ToUnixTimeMilliseconds();

        profile.SrvModified.Should().Be(expected);
        profile.SrvCreated.Should().Be(expected);
    }

    [Fact]
    public void Profile_WithUnparseableStartDate_DoesNotFabricateSrvTimestamps()
    {
        var profile = new Profile { Mills = 0, StartDate = "not-a-date" };

        profile.SrvModified.Should().BeNull();
    }
}
