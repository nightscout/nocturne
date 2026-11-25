using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>The builder's own state on a spec: kept as sent, its shape and size checked, and read by nothing.</summary>
public class ReportBuilderStateTests
{
    internal const string State = """
        { "kind": "workspace", "version": 1, "state": { "z": [3, 1, { "deep": null }], "a": "text", "n": 1.50, "on": true } }
        """;

    internal static string Spec(string? builder) => $$"""
        { "version": 1, "range": { "hours": 24 }, "panels": [ { "kind": "chart", "tracks": [ { "layers": [
          { "dataset": { "name": "glucose_series" }, "mark": "line" } ] } ] } ] {{(builder is null ? "" : $", \"builder\": {builder}")}} }
        """;

    private static ReportSpecValidation Validate(string json) =>
        RecordDatasetTests.Validator.Validate(JsonDocument.Parse(json).RootElement);

    private static string Normalised(ReportSpecValidation validation) =>
        JsonSerializer.Serialize(validation.Spec, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void The_builder_state_is_kept_as_sent_and_the_rest_normalises_as_without_it()
    {
        var with = Validate(Spec(State));
        var without = Validate(Spec(null));

        with.Errors.Should().BeEmpty();
        var normalised = JsonNode.Parse(Normalised(with))!.AsObject();
        JsonNode.DeepEquals(normalised["builder"], JsonNode.Parse(State)).Should().BeTrue();
        normalised["builder"]!["state"]!.ToJsonString().Should().Be("""{"z":[3,1,{"deep":null}],"a":"text","n":1.50,"on":true}""",
            "the state is written back member for member, numbers as sent");
        normalised.Remove("builder");
        normalised.ToJsonString().Should().Be(Normalised(without));
        Normalised(without).Should().NotContain("builder", "a spec without builder state normalises as before");
        with.Reads.Should().Be(without.Reads);
    }

    [Fact]
    public void Normalising_twice_gives_the_same_spec()
    {
        var once = Normalised(Validate(Spec(State)));

        Normalised(Validate(once)).Should().Be(once);
    }

    [Theory]
    [InlineData("[]", "builder", "Send 'builder' as an object with 'kind', 'version' and 'state'.")]
    [InlineData("""{ "kind": "", "version": 1, "state": {} }""", "builder.kind", "Send the builder's name as text of 1 to 40 characters.")]
    [InlineData("""{ "kind": "a-builder-whose-name-runs-past-forty-letters", "version": 1, "state": {} }""", "builder.kind", "Send the builder's name as text of 1 to 40 characters.")]
    [InlineData("""{ "kind": "workspace", "version": 0, "state": {} }""", "builder.version", "Send the builder's format version as a whole number from 1.")]
    [InlineData("""{ "kind": "workspace", "version": "1", "state": {} }""", "builder.version", "Send the builder's format version as a whole number from 1.")]
    [InlineData("""{ "kind": "workspace", "version": 1.5, "state": {} }""", "builder.version", "Send the builder's format version as a whole number from 1.")]
    [InlineData("""{ "kind": "workspace", "version": 1, "state": [] }""", "builder.state", "Send the builder's state as a JSON object.")]
    [InlineData("""{ "kind": "workspace", "version": 1 }""", "builder.state", "Send the builder's state as a JSON object.")]
    public void A_builder_of_another_shape_is_refused(string builder, string path, string message)
    {
        Validate(Spec(builder)).Errors.Should().ContainSingle().Which.Should().Be(new ReportSpecError(path, message));
    }

    [Fact]
    public void A_member_the_builder_does_not_list_is_refused()
    {
        var errors = Validate(Spec("""{ "kind": "workspace", "version": 1, "state": {}, "owner": "x" }""")).Errors;

        errors.Should().ContainSingle().Which.Path.Should().Be("builder.owner");
    }

    [Fact]
    public void The_builder_has_its_own_32_kB_beside_the_specs_16_kB()
    {
        static string Builder(int padding) => $$"""{ "kind": "workspace", "version": 1, "state": { "pad": "{{new string('x', padding)}}" } }""";

        var near = JsonDocument.Parse(Spec(Builder(31 * 1024))).RootElement;
        ReportSpec.WithinLimit(near).Should().BeTrue("the builder counts against its own limit, not the spec's");
        Validate(near.GetRawText()).Errors.Should().BeEmpty();

        Validate(Spec(Builder(32 * 1024))).Errors.Should().ContainSingle().Which
            .Should().Be(new ReportSpecError("builder", "The builder state is too large. It can be at most 32 kB."));

        var bigSpec = JsonDocument.Parse(Spec(Builder(10)).Replace("\"version\": 1,", $"\"version\": 1, \"title\": \"{new string('a', ReportSpec.MaxSpecBytes)}\",")).RootElement;
        ReportSpec.WithinLimit(bigSpec).Should().BeFalse("the rest of the spec keeps its 16 kB");
    }

    [Fact]
    public async Task A_run_with_builder_state_reads_and_gives_what_it_gives_without_it()
    {
        async Task<(string Result, int Reads, IReadOnlyList<string> Audited)> Run(string? builder)
        {
            var (runner, context, log) = new RecordDatasetTests.Harness().Arrange(Scope.GlucoseRead);
            var result = await runner.RunAsync(Validate(Spec(builder)).Spec!, context, CancellationToken.None);
            return (JsonSerializer.Serialize(result), context.Cache.Reads, log.Datasets);
        }

        var with = await Run(State);
        var without = await Run(null);

        with.Result.Should().Be(without.Result, "the result, its spec echo included, carries no builder state");
        with.Result.Should().NotContain("workspace");
        with.Reads.Should().Be(without.Reads);
        with.Audited.Should().Equal(without.Audited);
    }
}
