using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

public class ApsSnapshotReasonTests
{
    [Fact]
    public void ReadsTheSuggestionsReason()
    {
        var snapshot = new ApsSnapshot
        {
            SuggestedJson = """{"bg":129,"reason":"COB: 0, Dev: 5, BGI: -2; Eventual BG 95 < 100"}""",
            EnactedJson = """{"reason":"enacted text"}""",
        };

        snapshot.Reason.Should().Be("COB: 0, Dev: 5, BGI: -2; Eventual BG 95 < 100");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"bg":129}""")]
    [InlineData("""{"reason":""}""")]
    public void FallsBackToTheEnactedReason(string? suggestedJson)
    {
        var snapshot = new ApsSnapshot
        {
            SuggestedJson = suggestedJson,
            EnactedJson = """{"reason":"enacted text"}""",
        };

        snapshot.Reason.Should().Be("enacted text");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"bg":129}""")]
    [InlineData("""{"reason":42}""")]
    [InlineData("""["reason"]""")]
    [InlineData("not json")]
    public void IsNullWhenNeitherBlobCarriesAReason(string? json)
    {
        var snapshot = new ApsSnapshot { SuggestedJson = json, EnactedJson = json };

        snapshot.Reason.Should().BeNull();
    }

    [Fact]
    public void IsWrittenToTheWire()
    {
        var snapshot = new ApsSnapshot { SuggestedJson = """{"reason":"no temp required"}""" };

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        JsonDocument.Parse(json).RootElement.GetProperty("reason").GetString().Should().Be("no temp required");
    }
}
