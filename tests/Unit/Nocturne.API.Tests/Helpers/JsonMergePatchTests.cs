using FluentAssertions;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private static string Merge(string target, string patch)
    {
        using var body = JsonDocument.Parse(patch);
        return JsonMergePatch.Merge(JsonNode.Parse(target), body.RootElement)!.ToJsonString();
    }

    [Fact]
    public void Merge_a_nested_object_merges_rather_than_replaces()
    {
        Merge("""{"a":{"x":1,"y":2},"b":3}""", """{"a":{"y":9}}""")
            .Should().Be("""{"a":{"x":1,"y":9},"b":3}""");
    }

    [Fact]
    public void Merge_a_null_deletes_the_property_at_any_depth()
    {
        Merge("""{"a":{"x":1,"y":2},"b":3}""", """{"a":{"x":null},"b":null}""")
            .Should().Be("""{"a":{"y":2}}""");
    }

    [Fact]
    public void Merge_an_absent_property_is_kept()
    {
        Merge("""{"a":1,"b":2}""", "{}").Should().Be("""{"a":1,"b":2}""");
    }

    [Fact]
    public void Merge_a_list_replaces_the_stored_one_whole()
    {
        Merge("""{"tags":["a","b"]}""", """{"tags":["c"]}""").Should().Be("""{"tags":["c"]}""");
    }

    [Fact]
    public void Merge_an_object_over_a_scalar_replaces_it_and_drops_the_nulls_inside()
    {
        Merge("""{"a":1}""", """{"a":{"x":2,"y":null}}""").Should().Be("""{"a":{"x":2}}""");
    }

    [Fact]
    public void Merge_over_nothing_builds_the_object_from_the_patch()
    {
        using var body = JsonDocument.Parse("""{"a":{"x":1}}""");

        JsonMergePatch.Merge(null, body.RootElement)!.ToJsonString().Should().Be("""{"a":{"x":1}}""");
    }

    private sealed class Outer
    {
        public Inner Child { get; set; } = new();
        public Inner? Maybe { get; set; }
        public List<Inner> Items { get; set; } = [];
        public Dictionary<string, Inner> ByName { get; set; } = [];
        public JsonObject? Free { get; set; }
        public int Count { get; set; }
    }

    private sealed class Inner
    {
        [JsonPropertyName("custom")]
        public int Named { get; set; }

        public int Plain { get; set; }
    }

    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static void EnsureNames(string patch)
    {
        using var body = JsonDocument.Parse(patch);
        JsonMergePatch.EnsureNamesOf(typeof(Outer), body.RootElement, Camel);
    }

    [Theory]
    [InlineData("""{"child":{"custom":1,"plain":2},"count":3}""")]
    [InlineData("""{"maybe":{"plain":2},"child":null}""")]
    [InlineData("""{"items":[{"custom":1},{"plain":2}]}""")]
    [InlineData("""{"byName":{"AnyKeyAtAll":{"plain":1}}}""")]
    [InlineData("""{"free":{"Whatever":{"Goes":1}}}""")]
    public void EnsureNamesOf_accepts_the_json_names_of_the_type_at_every_depth(string patch)
    {
        var act = () => EnsureNames(patch);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("""{"Child":{}}""", "Child")]
    [InlineData("""{"child":{"Plain":1}}""", "child.Plain")]
    [InlineData("""{"child":{"named":1}}""", "child.named")]
    [InlineData("""{"maybe":{"nope":1}}""", "maybe.nope")]
    [InlineData("""{"items":[{"plain":1},{"Plain":1}]}""", "items[1].Plain")]
    [InlineData("""{"byName":{"a":{"Plain":1}}}""", "byName.a.Plain")]
    [InlineData("""{"unknown":1}""", "unknown")]
    public void EnsureNamesOf_refuses_a_key_that_is_not_a_json_name_and_names_it(string patch, string path)
    {
        var act = () => EnsureNames(patch);

        act.Should().Throw<ArgumentException>().WithMessage($"{path} *");
    }
}
