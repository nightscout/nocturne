using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Nocturne.API.Configuration;

/// <summary>
/// Adds <c>x-scalar-sdk-installation</c>, the "Install the SDK" panel Scalar renders above the
/// Nocturne API reference.
/// </summary>
public sealed class ScalarExtensionsDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.DocumentName == "nocturne")
        {
            document.Extensions ??= new Dictionary<string, IOpenApiExtension>();
            document.Extensions["x-scalar-sdk-installation"] = new JsonNodeExtension(
                BuildSdkInstallation());
        }

        return Task.CompletedTask;
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
                ".package(url: \"https://github.com/nightscout/nocturne-swift\", from: \"0.1.0\")",
        },
        new JsonObject
        {
            ["lang"] = "Rust",
            ["description"] = "Rust client (crates.io):",
            ["source"] = "cargo add nocturne-rs",
        },
    };
}
