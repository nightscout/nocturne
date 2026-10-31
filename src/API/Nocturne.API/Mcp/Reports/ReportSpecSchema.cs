using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
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

    public const string PatchDescription =
        "What to change: 'range' for every panel, or 'panels' with each panel's id and its new range or dataset parameters.";

    public static JsonElement InputSchema { get; } = Build(nameof(Shape));

    /// <summary><c>refine_report</c>'s input schema: the spec as <see cref="InputSchema"/> has it, and a <see cref="ReportSpecPatch"/>.</summary>
    public static JsonElement RefineInputSchema { get; } = Build(nameof(RefineShape));

    /// <summary><c>render_report_image</c>'s input schema: the spec as <see cref="InputSchema"/> has it, and the image arguments.</summary>
    public static JsonElement ImageInputSchema { get; } = Build(nameof(ImageShape));

    public const string PanelIdDescription =
        "The id of the one panel to draw. Without it, every chart panel is drawn, at most 4. Stat tiles and tables have no image.";

    public const string WidthDescription = "The image's width in pixels, 320 to 2400. Default 1200.";

    public const string HeightDescription =
        "The image's height in pixels, 200 to 1600. Default: each chart's own (600 for most, 240 for one stacked bar, 800 for a comparison).";

    public const string ThemeDescription = "light (the default, for a document on white) or dark.";

    public const string PixelRatioDescription = "1 (the default) or 2 for a sharper image at twice the pixels in each direction and about four times the size.";

    /// <summary>A dataset's parameters as the SDK describes a tool argument of that type.</summary>
    public static JsonElement ParametersSchema(Type parametersType) =>
        AIJsonUtilities.CreateJsonSchema(parametersType, serializerOptions: McpJsonUtilities.DefaultOptions);

    private static JsonElement Build(string shape)
    {
        var method = typeof(ReportSpecSchema).GetMethod(shape, BindingFlags.NonPublic | BindingFlags.Static)!;
        var generated = McpServerTool.Create(method, (object?)null, new McpServerToolCreateOptions()).ProtocolTool.InputSchema;

        // The one constraint no attribute expresses: the version is a constant.
        var schema = JsonNode.Parse(generated.GetRawText())!;
        var version = schema["properties"]!["spec"]!["properties"]!["version"]!.AsObject();
        version["const"] = ReportSpec.CurrentVersion;
        return JsonSerializer.SerializeToElement(schema);
    }

    private static void Shape([Description(SpecDescription)] ReportSpec spec) => _ = spec;

    private static void ImageShape(
        [Description(SpecDescription)] ReportSpec spec,
        [Description(PanelIdDescription)] string? panelId,
        [Description(WidthDescription), Range(320, 2400)] int? width,
        [Description(HeightDescription), Range(200, 1600)] int? height,
        [Description(ThemeDescription), AllowedValues("light", "dark")] string? theme,
        [Description(PixelRatioDescription), Range(1, 2)] int? devicePixelRatio) =>
        _ = (spec, panelId, width, height, theme, devicePixelRatio);

    private static void RefineShape(
        [Description("The normalised spec render_report returned.")] ReportSpec spec,
        [Description(PatchDescription)] ReportSpecPatch patch) => _ = (spec, patch);
}
