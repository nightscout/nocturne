using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Nocturne.API.Configuration;

/// <summary>
/// Adds Scalar-specific OpenAPI extensions to the document:
/// <c>x-tagGroups</c> for sidebar grouping and <c>x-scalar-sdk-installation</c>
/// for the "Install the SDK" panel rendered above the API reference.
/// </summary>
public sealed class ScalarExtensionsDocumentTransformer : IOpenApiDocumentTransformer
{
    private static readonly Dictionary<string, string[]> NocturneTagGroups = new()
    {
        ["Authentication & Identity"] = ["Authentication", "OIDC Discovery", "Identity"],
        ["Health Data"] = ["Glucose", "Treatments", "Health", "Devices", "State Spans"],
        ["Insights & Alerting"] = ["Analytics", "Monitoring"],
        ["Configuration"] = ["Profiles", "Connectors", "Metadata"],
        ["Administration"] = ["Platform", "PlatformAdmin", "TenantAdmin"],
    };

    private static readonly Dictionary<string, string[]> NightscoutTagGroups = new()
    {
        ["Nightscout Legacy API"] = ["V1", "V2", "V3"],
    };

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Extensions ??= new Dictionary<string, IOpenApiExtension>();

        var groups = context.DocumentName == "nightscout"
            ? NightscoutTagGroups
            : NocturneTagGroups;

        var usedTags = CollectUsedTags(document);

        var tagGroupsArray = new JsonArray();
        foreach (var (groupName, tags) in groups)
        {
            var present = tags.Where(usedTags.Contains).ToArray();
            if (present.Length == 0) continue;

            var tagsArray = new JsonArray();
            foreach (var tag in present)
                tagsArray.Add(JsonValue.Create(tag));

            tagGroupsArray.Add(new JsonObject
            {
                ["name"] = groupName,
                ["tags"] = tagsArray,
            });
        }

        if (tagGroupsArray.Count > 0)
            document.Extensions["x-tagGroups"] = new JsonNodeExtension(tagGroupsArray);

        if (context.DocumentName == "nocturne")
        {
            document.Extensions["x-scalar-sdk-installation"] = new JsonNodeExtension(
                BuildSdkInstallation());
        }

        return Task.CompletedTask;
    }

    private static HashSet<string> CollectUsedTags(OpenApiDocument document)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (document.Paths is null) return used;

        foreach (var pathItem in document.Paths.Values)
        {
            if (pathItem.Operations is null) continue;
            foreach (var op in pathItem.Operations.Values)
            {
                if (op.Tags is null) continue;
                foreach (var tag in op.Tags)
                    if (tag is IOpenApiTag t && t.Name is not null)
                        used.Add(t.Name);
            }
        }
        return used;
    }

    private static JsonArray BuildSdkInstallation() => new()
    {
        new JsonObject
        {
            ["lang"] = "Node",
            ["description"] = "TypeScript / JavaScript client (npm):",
            ["source"] = "pnpm add @nightscoutfoundation/nocturne",
        },
        new JsonObject
        {
            ["lang"] = "C#",
            ["description"] = ".NET client (NuGet):",
            ["source"] = "dotnet add package NightscoutFoundation.Nocturne",
        },
        new JsonObject
        {
            ["lang"] = "Python",
            ["description"] = "Python client (PyPI):",
            ["source"] = "pip install nocturne-py",
        },
        new JsonObject
        {
            ["lang"] = "Java",
            ["description"] = "Java client (Maven):",
            ["source"] =
                "<dependency>\n"
                + "  <groupId>org.nightscoutfoundation</groupId>\n"
                + "  <artifactId>nocturne-java</artifactId>\n"
                + "  <version>0.1.0</version>\n"
                + "</dependency>",
        },
        new JsonObject
        {
            ["lang"] = "Kotlin",
            ["description"] = "Kotlin client (Gradle):",
            ["source"] = "implementation(\"org.nightscoutfoundation:nocturne:0.1.0\")",
        },
        new JsonObject
        {
            ["lang"] = "Swift",
            ["description"] = "Swift client (Swift Package Manager):",
            ["source"] =
                ".package(url: \"https://github.com/nightscout/nocturne\", from: \"0.1.0\")",
        },
    };
}
