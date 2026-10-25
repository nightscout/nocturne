using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.API.Helpers;
using Xunit;

namespace Nocturne.API.Tests.Helpers;

[Trait("Category", "Unit")]
public class JsonMergePatchTests
{
    private sealed class Record
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = [];

        [JsonPropertyName("shout")]
        public string? Shout => Name?.ToUpperInvariant();
    }

    private static Record Patch(Record target, string patch, Action<Record, JsonElement>? adjust = null)
    {
        using var body = JsonDocument.Parse(patch);
        JsonMergePatch.ApplyInPlace(target, body.RootElement, adjust);
        return target;
    }

    [Fact]
    public void A_patch_overlays_only_the_named_fields()
    {
        var target = new Record { Name = "before", Count = 3, Tags = ["a"] };

        Patch(target, """{"count": 7}""");

        target.Should().BeEquivalentTo(new { Name = "before", Count = 7, Tags = new[] { "a" } });
    }

    [Fact]
    public void A_patched_collection_replaces_the_stored_one_whole()
    {
        var target = new Record { Tags = ["a", "b"] };

        Patch(target, """{"tags": ["c"]}""");

        target.Tags.Should().Equal("c");
    }

    [Fact]
    public void A_computed_property_in_the_patch_is_ignored()
    {
        var target = new Record { Name = "before" };

        var act = () => Patch(target, """{"shout": "LOUD"}""");

        act.Should().NotThrow();
        target.Shout.Should().Be("BEFORE");
    }

    [Fact]
    public void The_adjustment_sees_the_merged_copy_before_it_lands()
    {
        var target = new Record { Name = "before", Count = 1 };

        Patch(target, """{"name": "after"}""", (patched, _) => patched.Count = 9);

        target.Should().BeEquivalentTo(new { Name = "after", Count = 9 });
    }
}
