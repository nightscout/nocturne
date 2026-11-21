using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

[Trait("Category", "Unit")]
public class KeptFieldTests
{
    private const string Key = "k";

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static Dictionary<string, object?> Held(object? value) => new() { [Key] = value };

    [Fact]
    public void Add_creates_the_record_when_the_value_is_present()
    {
        var record = KeptField.Add(null, Key, "x");

        record.Should().NotBeNull();
        record![Key].Should().Be("x");
    }

    [Fact]
    public void Add_keeps_the_other_keys_of_the_record()
    {
        var existing = new Dictionary<string, object?> { ["other"] = 1 };

        var record = KeptField.Add(existing, Key, 2.5);

        record.Should().BeSameAs(existing);
        record!["other"].Should().Be(1);
        record[Key].Should().Be(2.5);
    }

    [Fact]
    public void Add_leaves_a_null_record_null_and_a_record_unchanged_when_the_value_is_null()
    {
        KeptField.Add(null, Key, null).Should().BeNull();

        var existing = new Dictionary<string, object?> { ["other"] = 1 };
        KeptField.Add(existing, Key, null).Should().BeSameAs(existing);
        existing.Should().NotContainKey(Key);
    }

    [Fact]
    public void Add_keeps_a_false_flag_and_a_zero_number()
    {
        var record = KeptField.Add(KeptField.Add(null, "flag", false), "zero", 0.0);

        record!["flag"].Should().Be(false);
        record["zero"].Should().Be(0.0);
    }

    [Fact]
    public void String_reads_the_in_memory_and_the_persisted_string()
    {
        KeptField.String(Held("a"), Key).Should().Be("a");
        KeptField.String(Held(Json("\"b\"")), Key).Should().Be("b");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    public void String_rejects_a_persisted_value_that_is_not_a_string(string raw)
    {
        KeptField.String(Held(Json(raw)), Key).Should().BeNull();
    }

    [Fact]
    public void String_rejects_an_in_memory_value_that_is_not_a_string()
    {
        KeptField.String(Held(5), Key).Should().BeNull();
    }

    [Fact]
    public void String_is_null_for_a_null_record_a_missing_key_and_a_null_value()
    {
        KeptField.String(null, Key).Should().BeNull();
        KeptField.String(new Dictionary<string, object?>(), Key).Should().BeNull();
        KeptField.String(Held(null), Key).Should().BeNull();
    }

    [Fact]
    public void Double_reads_the_in_memory_double_and_the_persisted_number()
    {
        KeptField.Double(Held(1.5), Key).Should().Be(1.5);
        KeptField.Double(Held(Json("2.25")), Key).Should().Be(2.25);
        KeptField.Double(Held(Json("3")), Key).Should().Be(3);
    }

    [Theory]
    [InlineData("\"1.5\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void Double_rejects_a_persisted_value_that_is_not_a_number(string raw)
    {
        KeptField.Double(Held(Json(raw)), Key).Should().BeNull();
    }

    [Fact]
    public void Double_rejects_an_in_memory_value_that_is_not_a_double()
    {
        KeptField.Double(Held(1), Key).Should().BeNull();
        KeptField.Double(Held("1.5"), Key).Should().BeNull();
    }

    [Fact]
    public void Double_is_null_for_a_null_record_and_a_missing_key()
    {
        KeptField.Double(null, Key).Should().BeNull();
        KeptField.Double(new Dictionary<string, object?>(), Key).Should().BeNull();
    }

    [Fact]
    public void Bool_reads_the_in_memory_and_the_persisted_flag()
    {
        KeptField.Bool(Held(true), Key).Should().BeTrue();
        KeptField.Bool(Held(false), Key).Should().BeFalse();
        KeptField.Bool(Held(Json("true")), Key).Should().BeTrue();
        KeptField.Bool(Held(Json("false")), Key).Should().BeFalse();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"true\"")]
    [InlineData("null")]
    public void Bool_rejects_a_persisted_value_that_is_not_a_flag(string raw)
    {
        KeptField.Bool(Held(Json(raw)), Key).Should().BeNull();
    }

    [Fact]
    public void Bool_rejects_an_in_memory_value_that_is_not_a_flag()
    {
        KeptField.Bool(Held("true"), Key).Should().BeNull();
        KeptField.Bool(Held(1), Key).Should().BeNull();
    }

    [Fact]
    public void Bool_is_null_for_a_null_record_and_a_missing_key()
    {
        KeptField.Bool(null, Key).Should().BeNull();
        KeptField.Bool(new Dictionary<string, object?>(), Key).Should().BeNull();
    }

    [Fact]
    public void Present_is_null_for_null_and_for_a_json_null_or_undefined()
    {
        KeptField.Present(null).Should().BeNull();
        KeptField.Present(Json("null")).Should().BeNull();
        KeptField.Present(default(JsonElement)).Should().BeNull();
    }

    [Fact]
    public void Present_returns_any_other_value_as_it_is()
    {
        KeptField.Present("a").Should().Be("a");
        KeptField.Present(0).Should().Be(0);
        var element = Json("\"a\"");
        KeptField.Present(element).Should().Be(element);
    }

    [Fact]
    public void Json_is_null_for_null_and_for_a_json_null_or_undefined()
    {
        KeptField.Json(null).Should().BeNull();
        KeptField.Json(Json("null")).Should().BeNull();
        KeptField.Json(default(JsonElement)).Should().BeNull();
    }

    [Fact]
    public void Json_returns_a_persisted_element_with_its_kind_and_serialises_an_in_memory_value()
    {
        KeptField.Json(Json("12")).Should().NotBeNull();
        KeptField.Json(Json("12"))!.Value.ValueKind.Should().Be(JsonValueKind.Number);
        KeptField.Json("2026-01-01T00:00:00Z")!.Value.GetString().Should().Be("2026-01-01T00:00:00Z");
        KeptField.Json(5L)!.Value.GetInt64().Should().Be(5);
    }
}
