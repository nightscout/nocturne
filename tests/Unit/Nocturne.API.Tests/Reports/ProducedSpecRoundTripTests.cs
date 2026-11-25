using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Reports;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// Every spec the web workspace produces (its starters, each stored spec reopened and saved, and the edits its
/// Advanced drawers make), from <c>produced-specs.fixture.json</c>, which the web's <c>produced-specs.test.ts</c>
/// keeps in step with the workspace. Each is taken, and the form the API stores it in normalises to itself.
/// </summary>
public class ProducedSpecRoundTripTests
{
    private const string FixturePath = "src/Web/packages/app/src/lib/components/reports/custom/workspace/produced-specs.fixture.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ReportSpecValidator Validator = new(ReportSpecValidatorTests.Registry, TimeProvider.System);

    private static readonly Lazy<JsonObject> Fixture = new(() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), FixturePath)))!.AsObject());

    public static TheoryData<string> Names() => [.. Fixture.Value.Select(p => p.Key)];

    public static TheoryData<string> Reopened() =>
        [.. Fixture.Value.Select(p => p.Key).Where(n => n.StartsWith("reopened ", StringComparison.Ordinal))];

    [Fact]
    public void The_fixture_holds_the_starters_the_stored_specs_and_the_drawer_edits()
    {
        var names = Fixture.Value.Select(p => p.Key).ToList();
        names.Should().Contain(n => n.StartsWith("starter ", StringComparison.Ordinal));
        names.Should().Contain(n => n.StartsWith("reopened ", StringComparison.Ordinal));
        names.Should().Contain(n => n.StartsWith("given ", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void A_produced_spec_is_taken_and_its_normalised_form_normalises_to_itself(string name)
    {
        var sent = Fixture.Value[name]!;

        var normalised = Normalise(sent);
        var again = Normalise(normalised);

        Canonical(again).Should().Be(Canonical(normalised), "the spec the API stores opens and saves as it is");
        Canonical(normalised["builder"]).Should().Be(Canonical(sent["builder"]), "the builder's state is kept as sent");
    }

    /// <summary>
    /// A stored spec the workspace reopened saves as it was stored. A spec the workspace makes itself leaves out what
    /// the engine fills in (a panel's range, a track's height, a calculated layer's bindings), so it is not its own
    /// normalisation; its normalised form is, by the theory above.
    /// </summary>
    [Theory]
    [MemberData(nameof(Reopened))]
    public void A_reopened_stored_spec_normalises_to_the_spec_the_workspace_sent(string name)
    {
        var sent = Fixture.Value[name]!;

        Canonical(EngineView(Normalise(sent))).Should().Be(Canonical(EngineView(sent)));
    }

    /// <summary>The spec as the validate endpoint returns it, after it is checked to be taken.</summary>
    private static JsonNode Normalise(JsonNode spec)
    {
        var validation = Validator.Validate(ReportSpecRewriter.Current.Rewrite(JsonSerializer.SerializeToElement(spec)));
        validation.Errors.Should().BeEmpty(string.Join("\n", validation.Errors));
        return JsonNode.Parse(JsonSerializer.Serialize(validation.Spec, Json))!;
    }

    /// <summary>The spec as the engine reads it: no builder state and no null members, which it reads as absent.</summary>
    private static JsonNode? EngineView(JsonNode node)
    {
        var copy = node.DeepClone().AsObject();
        copy.Remove("builder");
        return WithoutNulls(copy);
    }

    private static JsonNode? WithoutNulls(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Where(p => p.Value is not null).Select(p =>
            KeyValuePair.Create(p.Key, WithoutNulls(p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(WithoutNulls)]),
        _ => node?.DeepClone(),
    };

    /// <summary>JSON text with object keys sorted and numbers written by value, so key order and 1 against 1.0 do not count.</summary>
    private static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        JsonValue v when v.GetValueKind() == JsonValueKind.Number =>
            v.GetValue<JsonElement>().GetDecimal().ToString("G29", CultureInfo.InvariantCulture),
        _ => node.ToJsonString(),
    };

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "Unit")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
