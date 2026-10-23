using System.Text.Json.Serialization;
using FluentAssertions;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

public class RedactionCoverageTests
{
    public sealed record Positional(string Name, int[] Values);

    public sealed class Shapes
    {
        public Positional? Record { get; set; }
        public string[]? Array { get; set; }
        public Dictionary<string, Positional>? Map { get; set; }
        public IReadOnlyList<Positional>? ReadOnlyList { get; set; }
        public HashSet<int>? Set { get; set; }
    }

    public sealed record Computed(string Name)
    {
        public int Length => Name.Length;
    }

    public sealed class Conditional
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Value { get; set; }

        [JsonIgnore]
        public string? Never { get; set; }
    }

    public sealed class Unsettable
    {
        public string? Value { get; }
    }

    public sealed class Opaque
    {
        public IComparable? Value { get; set; }
    }

    [Fact]
    public void Populated_FillsEveryShape()
    {
        var shapes = RedactionCoverage.Populated<Shapes>();

        shapes.Record.Should().BeEquivalentTo(new Positional("x", [1]));
        shapes.Array.Should().Equal("x");
        shapes.Map.Should().ContainKey("x").WhoseValue.Values.Should().Equal(1);
        shapes.ReadOnlyList.Should().ContainSingle().Which.Name.Should().Be("x");
        shapes.Set.Should().Equal(1);
    }

    [Fact]
    public void Populated_ThrowsOnAShapeItCannotFill()
    {
        var populate = () => RedactionCoverage.Populated<Opaque>();

        populate.Should().Throw<InvalidOperationException>().WithMessage("*IComparable*");
    }

    [Fact]
    public void AssertWithheld_ReachesDictionaryValues()
    {
        var shapes = new Shapes { Map = new() { ["k"] = new Positional("leak", []) } };

        var assert = () => RedactionCoverage.AssertWithheld(shapes, [typeof(Shapes), typeof(Positional)], ["Shapes.Map"]);

        assert.Should().Throw<Exception>().WithMessage("*Positional.Name*");
    }

    [Fact]
    public void AssertWithheld_ChecksAComputedProperty()
    {
        var assert = () => RedactionCoverage.AssertWithheld(
            new Computed("leak"), [typeof(Computed)], ["Computed.Name"]);

        assert.Should().Throw<Exception>().WithMessage("*Computed.Length*");
    }

    [Fact]
    public void AssertWithheld_ChecksAPropertyIgnoredOnlyWhenNull()
    {
        var conditional = RedactionCoverage.Populated<Conditional>();

        conditional.Never.Should().BeNull("a property never served is not populated");
        var assert = () => RedactionCoverage.AssertWithheld(conditional, [typeof(Conditional)], []);
        assert.Should().Throw<Exception>().WithMessage("*Conditional.Value*").Which.Message.Should().NotContain("Never");
    }

    [Fact]
    public void Populated_ThrowsOnAServedPropertyItCannotSet()
    {
        var populate = () => RedactionCoverage.Populated<Unsettable>();

        populate.Should().Throw<InvalidOperationException>().WithMessage("*Unsettable.Value*");
    }
}
