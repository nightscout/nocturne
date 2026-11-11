using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.OpenApi;
using Nocturne.API.OpenApi;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.OpenApi;

/// <summary>
/// The runtime documents Scalar renders, read as a client reads them.
/// </summary>
public sealed class RuntimeOpenApiDocumentTests : IClassFixture<AuthenticationTestFactory>
{
    private readonly AuthenticationTestFactory _factory;

    public RuntimeOpenApiDocumentTests(AuthenticationTestFactory factory) => _factory = factory;

    private async Task<JsonElement> DocumentAsync(string name)
    {
        var response = await _factory.CreateClient().GetAsync($"/openapi/{name}.json");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    /// <summary>Schemas of the models that accept quoted numbers on purpose.</summary>
    private static readonly HashSet<string> QuotedNumberSchemas = typeof(Food).Assembly.GetTypes()
        .Where(t => t.GetCustomAttribute<JsonNumberHandlingAttribute>()?.Handling
            .HasFlag(JsonNumberHandling.AllowReadingFromString) == true)
        .Select(t => $"$.components.schemas.{t.Name}.")
        .ToHashSet();

    [Theory]
    [InlineData("nocturne")]
    [InlineData("nightscout")]
    public async Task Only_a_model_that_accepts_quoted_numbers_types_a_number_as_a_string(string name)
    {
        var mixed = new List<string>();
        Walk(await DocumentAsync(name), "$", mixed);

        mixed.Where(path => !QuotedNumberSchemas.Any(path.StartsWith)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_model_that_accepts_quoted_numbers_keeps_the_string_form()
    {
        var portion = (await DocumentAsync("nocturne"))
            .GetProperty("components").GetProperty("schemas").GetProperty("Food")
            .GetProperty("properties").GetProperty("portion");

        portion.GetProperty("type").EnumerateArray().Select(t => t.GetString())
            .Should().Contain(["number", "string"]);
    }

    [Fact]
    public async Task An_operation_publishes_its_summary_and_not_its_remarks()
    {
        var operation = (await DocumentAsync("nocturne"))
            .GetProperty("paths").GetProperty("/api/v4/Activity").GetProperty("get");

        operation.GetProperty("summary").GetString().Should().Be("Get activities");
        operation.GetProperty("description").GetString().Should().Be("Get activity records with pagination");
    }

    [Fact]
    public async Task Paging_parameters_are_integers()
    {
        var parameters = (await DocumentAsync("nocturne"))
            .GetProperty("paths").GetProperty("/api/v4/Activity").GetProperty("get")
            .GetProperty("parameters").EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("schema"));

        foreach (var name in new[] { "limit", "offset" })
        {
            parameters[name].GetProperty("type").GetString().Should().Be("integer");
            parameters[name].TryGetProperty("pattern", out _).Should().BeFalse();
        }
    }

    private static void Walk(JsonElement element, string path, List<string> mixed)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array)
                {
                    var types = type.EnumerateArray().Select(t => t.GetString()).ToHashSet();
                    if (types.Contains("string") && (types.Contains("integer") || types.Contains("number")))
                        mixed.Add(path);
                }

                foreach (var property in element.EnumerateObject())
                    Walk(property.Value, $"{path}.{property.Name}", mixed);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                    Walk(item, $"{path}[{i++}]", mixed);
                break;
        }
    }
}

public sealed class NumericSchemaTransformerTests
{
    [Theory]
    [InlineData(JsonSchemaType.Integer | JsonSchemaType.String, JsonSchemaType.Integer)]
    [InlineData(JsonSchemaType.Number | JsonSchemaType.String, JsonSchemaType.Number)]
    [InlineData(
        JsonSchemaType.Integer | JsonSchemaType.String | JsonSchemaType.Null,
        JsonSchemaType.Integer | JsonSchemaType.Null)]
    public void A_number_readable_from_a_string_is_published_as_a_number(
        JsonSchemaType generated, JsonSchemaType published)
    {
        var schema = new OpenApiSchema { Type = generated, Pattern = @"^-?(?:0|[1-9]\d*)$" };

        NumericSchemaTransformer.Collapse(schema);

        schema.Type.Should().Be(published);
        schema.Pattern.Should().BeNull();
    }

    [Theory]
    [InlineData(JsonSchemaType.String)]
    [InlineData(JsonSchemaType.String | JsonSchemaType.Null)]
    public void A_string_keeps_its_pattern(JsonSchemaType type)
    {
        var schema = new OpenApiSchema { Type = type, Pattern = "^[a-z]+$" };

        NumericSchemaTransformer.Collapse(schema);

        schema.Type.Should().Be(type);
        schema.Pattern.Should().Be("^[a-z]+$");
    }
}
