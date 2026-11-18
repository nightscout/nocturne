using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Nocturne.API.Configuration;
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

    private static List<(string Name, List<string> Tags)> Groups(JsonElement document) =>
        document.GetProperty("x-tagGroups").EnumerateArray()
            .Select(group => (
                group.GetProperty("name").GetString()!,
                group.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToList()))
            .ToList();

    private static IEnumerable<string> Pages(JsonElement document) =>
        document.GetProperty("tags").EnumerateArray()
            .Select(tag => tag.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "")
            .Append(document.GetProperty("info").GetProperty("description").GetString() ?? "");

    [Theory]
    [InlineData("nocturne")]
    [InlineData("nightscout")]
    public async Task Every_sidebar_entry_has_a_page_and_a_group(string name)
    {
        var document = await DocumentAsync(name);
        var grouped = Groups(document).SelectMany(group => group.Tags).ToHashSet();
        var tags = document.GetProperty("tags").EnumerateArray().ToList();

        tags.Where(tag => !tag.TryGetProperty("description", out var description)
                || string.IsNullOrWhiteSpace(description.GetString()))
            .Select(tag => tag.GetProperty("name").GetString())
            .Should().BeEmpty("a controller needs a class <summary>, and a page needs a body");

        tags.Select(tag => tag.GetProperty("name").GetString())
            .Where(tag => !grouped.Contains(tag))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("nocturne")]
    [InlineData("nightscout")]
    public async Task Every_operation_is_listed_under_its_controller(string name)
    {
        var document = await DocumentAsync(name);
        var declared = document.GetProperty("tags").EnumerateArray()
            .Select(tag => tag.GetProperty("name").GetString())
            .ToHashSet();

        var operations = document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Where(operation => operation.Value.TryGetProperty("responses", out _))
            .Select(operation => operation.Value.GetProperty("tags").EnumerateArray()
                .Select(tag => tag.GetString()).ToList())
            .ToList();

        operations.Should().OnlyContain(tags => tags.Count == 1 && declared.Contains(tags[0]));
    }

    [Theory]
    [InlineData("nocturne")]
    [InlineData("nightscout")]
    public async Task The_sidebar_is_the_declared_order_and_every_area_has_an_overview(string name)
    {
        var groups = Groups(await DocumentAsync(name));
        var declared = TagDescriptionDocumentTransformer.Sidebar[name];

        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        var displayNames = TagDescriptionLoader.Load(environment)
            .Where(page => page.Document == name && page.DisplayName is not null)
            .ToDictionary(page => page.TagName, page => page.DisplayName!);

        groups.Select(group => group.Name).Should().Equal(
            declared.Select(entry => displayNames.GetValueOrDefault(entry.Group, entry.Group)),
            "a [Tags] area needs a place in TagDescriptionDocumentTransformer.Sidebar, and a controller needs a [Tags] area");

        var areas = declared.Where(entry => entry.Pages is null).Select(entry => entry.Group);
        var overviews = groups.Select(group => group.Tags[0]).ToHashSet();

        areas.Where(area => !overviews.Contains(TagDescriptionDocumentTransformer.OverviewTag(area)))
            .Should().BeEmpty("each area needs an overview page in docs/api-descriptions");
    }

    [Theory]
    [InlineData("nocturne")]
    [InlineData("nightscout")]
    public async Task Every_page_on_disk_is_shown(string name)
    {
        var shown = (await DocumentAsync(name)).GetProperty("tags").EnumerateArray()
            .Select(tag => tag.GetProperty("name").GetString())
            .ToHashSet();

        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();

        TagDescriptionLoader.Load(environment)
            .Where(page => page.Document == name)
            .Where(page => !shown.Contains(page.TagName)
                && !shown.Contains(TagDescriptionDocumentTransformer.OverviewTag(page.TagName)))
            .Select(page => page.TagName)
            .Should().BeEmpty("a page is a standalone entry of a Sidebar group or the overview of an area");
    }

    [Theory]
    [InlineData("nocturne")]
    [InlineData("nightscout")]
    public async Task A_link_between_pages_names_a_tag_of_the_document(string name)
    {
        var document = await DocumentAsync(name);

        var anchors = document.GetProperty("tags").EnumerateArray()
            .Select(tag => ControllerDocs.Slug(tag.GetProperty("name").GetString()!))
            .ToHashSet();

        Pages(document)
            .SelectMany(page => Regex.Matches(page, @"\(#(?<document>[\w-]+)/tag/(?<tag>[^)\s]+)\)"))
            .Where(link => link.Groups["document"].Value != name || !anchors.Contains(link.Groups["tag"].Value))
            .Select(link => link.Value)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task A_path_named_in_a_page_is_a_path_of_the_api()
    {
        var documents = new[] { await DocumentAsync("nocturne"), await DocumentAsync("nightscout") };

        var paths = documents
            .SelectMany(document => document.GetProperty("paths").EnumerateObject())
            .Select(path => path.Name)
            .ToList();

        var named = documents.SelectMany(Pages)
            .SelectMany(page => Regex.Matches(page, @"(?<![\w.>])/(?:api|\.well-known)/[\w./{}-]*"))
            .Select(match => match.Value.TrimEnd('/', '.', '{'))
            .Distinct();

        named.Where(path => !paths.Any(real => real.Equals(path, StringComparison.OrdinalIgnoreCase)
                || real.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("nocturne")]
    [InlineData("nightscout")]
    public async Task A_page_carries_no_response_example(string name)
    {
        Pages(await DocumentAsync(name)).Should().NotContain(page => page.Contains("```json"),
            "a response shape comes from the schema of its operation");
    }

    [Theory]
    [InlineData("nocturne")]
    [InlineData("nightscout")]
    public async Task A_resources_block_names_a_sidebar_group(string name)
    {
        var document = await DocumentAsync(name);
        var groups = Groups(document).Select(group => group.Name).ToHashSet();

        Pages(document)
            .SelectMany(page => Regex.Matches(page, @"^```resources[ \t]*(?<group>[^\r\n]*)", RegexOptions.Multiline))
            .Select(block => block.Groups["group"].Value.Trim())
            .Where(group => !groups.Contains(group))
            .Should().BeEmpty();
    }

    [Fact]
    public void The_security_scheme_describes_every_scope_a_client_may_request()
    {
        SecuritySchemeDefinitions.OAuth2Scopes.Keys.Should().BeEquivalentTo(Nocturne.Core.Models.Authorization.Scope.ValidRequestScopes);
    }

    [Fact]
    public async Task The_introduction_gives_the_sidebar_its_headings()
    {
        var introduction = (await DocumentAsync("nocturne"))
            .GetProperty("info").GetProperty("description").GetString();

        introduction.Should().Contain("## Quick start").And.Contain("## Conventions");
    }

    [Fact]
    public async Task An_operation_publishes_its_summary_and_not_its_remarks()
    {
        var operation = (await DocumentAsync("nocturne"))
            .GetProperty("paths").GetProperty("/api/v4/Activity").GetProperty("get");

        operation.GetProperty("summary").GetString().Should().Be("Get activities");
        operation.GetProperty("description").GetString().Should()
            .StartWith("Get activity records with pagination")
            .And.NotContain("ActivityReadScopeGuard")
            .And.NotContain("ClampMergedPage");
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
