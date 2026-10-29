using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Nocturne.API.Mcp.Reports;

/// <summary>
/// <c>render_report</c>'s input schema: what the SDK generates for a <see cref="ReportSpec"/>
/// argument, with <c>dataset</c> an <c>anyOf</c> keyed by a <c>name</c> constant per
/// <see cref="ReportDatasetParameters"/> subtype and <c>primitive</c> an enum.
/// </summary>
/// <remarks>
/// The tool itself takes the spec as raw JSON so that <see cref="ReportSpecValidator"/>, not the
/// serializer, reports what is wrong with it; this puts the typed schema back on the tool.
/// </remarks>
internal static class ReportSpecSchema
{
    public const string SpecDescription =
        "The report to draw. Each panel names a dataset and a primitive; call describe_dataset first to "
        + "see a dataset's fields, units, allowed primitives and longest range.";

    public static JsonElement InputSchema { get; } = Build();

    /// <summary>A dataset's parameters as the SDK describes a tool argument of that type.</summary>
    public static JsonElement ParametersSchema(Type parametersType) =>
        AIJsonUtilities.CreateJsonSchema(parametersType, serializerOptions: McpJsonUtilities.DefaultOptions);

    private static JsonElement Build()
    {
        var method = typeof(ReportSpecSchema).GetMethod(nameof(Shape), BindingFlags.NonPublic | BindingFlags.Static)!;
        var generated = McpServerTool.Create(method, (object?)null, new McpServerToolCreateOptions()).ProtocolTool.InputSchema;

        // The one constraint no attribute expresses: the version is a constant.
        var schema = JsonNode.Parse(generated.GetRawText())!;
        var version = schema["properties"]!["spec"]!["properties"]!["version"]!.AsObject();
        version["const"] = ReportSpec.CurrentVersion;
        return JsonSerializer.SerializeToElement(schema);
    }

    private static void Shape([Description(SpecDescription)] ReportSpec spec) => _ = spec;
}
