using System.Globalization;
using System.Text;
using ModelContextProtocol.Protocol;
using Nocturne.API.Mcp.Reports;

namespace Nocturne.API.Mcp.Images;

/// <summary>The size, theme and pixel ratio of a report's images, and the one panel to draw when named.</summary>
internal sealed record ReportImageOptions(string? PanelId, int Width, int Height, string Theme, int DevicePixelRatio);

/// <summary>
/// A report as images: the spec runs through <see cref="ReportRenderer"/> exactly as for
/// <c>render_report</c> (the caller's scopes, clamp and reader, and one audit row from the same read
/// log), then each chart panel is drawn as a PNG by <see cref="IReportImageClient"/>. The result is
/// the summary text followed by one image per panel, with no <c>structuredContent</c>: it is not a view.
/// </summary>
internal sealed class ReportImageRenderer(ReportRenderer renderer, IReportImageClient client)
{
    public const int DefaultWidth = 1200;
    public const int DefaultHeight = 600;
    public const int MinWidth = 320;
    public const int MaxWidth = 2400;
    public const int MinHeight = 200;
    public const int MaxHeight = 1600;

    /// <summary>The most panels drawn when none is named.</summary>
    public const int MaxPanels = 4;

    /// <summary>The most base64 image data one result carries, about 1.5 MB.</summary>
    public const int MaxTotalBase64Bytes = 1_536 * 1024;

    /// <summary>The primitives that have an image. Stat tiles and tables are text, which the summary carries.</summary>
    public static bool IsChart(ReportPrimitive primitive) => primitive is ReportPrimitive.TimeSeries
        or ReportPrimitive.RangeBands or ReportPrimitive.Bar or ReportPrimitive.Comparison or ReportPrimitive.GlucoseChart;

    /// <summary>Every chart panel at the default size, light, ratio 1: what a preset's <c>asImage</c> draws.</summary>
    public static ReportImageOptions Defaults { get; } = new(null, DefaultWidth, DefaultHeight, "light", 1);

    /// <summary>The image arguments, defaulted and checked; every error is listed in one refusal.</summary>
    /// <exception cref="McpInvalidArgumentsException">An argument is out of range.</exception>
    public static ReportImageOptions ReadOptions(string tool, string? panelId, int? width, int? height, string? theme, int? devicePixelRatio)
    {
        var errors = new List<string>();
        if (width is < MinWidth or > MaxWidth)
            errors.Add($"width: Send a whole number from {MinWidth} to {MaxWidth}.");
        if (height is < MinHeight or > MaxHeight)
            errors.Add($"height: Send a whole number from {MinHeight} to {MaxHeight}.");
        if (theme is not (null or "light" or "dark"))
            errors.Add("theme: Send light or dark.");
        if (devicePixelRatio is not (null or 1 or 2))
            errors.Add("devicePixelRatio: Send 1 or 2.");
        if (panelId is { Length: 0 or > 64 })
            errors.Add("panelId: Send the id of one panel in the spec.");
        if (errors.Count > 0)
            throw new McpInvalidArgumentsException(
                $"The image arguments are not valid. Change these and call {tool} again:\n" + string.Join('\n', errors.Select(e => $"- {e}")));

        return new ReportImageOptions(panelId, width ?? DefaultWidth, height ?? DefaultHeight, theme ?? "light", devicePixelRatio ?? 1);
    }

    /// <summary>Refuses, before anything is read, a spec with no panel this can draw, or a named panel it does not hold or cannot draw.</summary>
    /// <exception cref="McpInvalidArgumentsException">No panel can be drawn.</exception>
    public static void CheckPanels(ReportSpec spec, ReportImageOptions options, string tool)
    {
        if (options.PanelId is { } id)
        {
            var panel = spec.Panels.FirstOrDefault(p => p.Id == id)
                ?? throw new McpInvalidArgumentsException(
                    $"panelId: The spec has no panel '{id}'. Send one of: {string.Join(", ", spec.Panels.Select(p => p.Id))}.");
            if (!IsChart(panel.Primitive))
                throw new McpInvalidArgumentsException(
                    $"panelId: Panel '{id}' is drawn as {Name(panel.Primitive)}, which has no image. Its figures are in the text of render_report.");
            return;
        }

        if (!spec.Panels.Any(p => IsChart(p.Primitive)))
            throw new McpInvalidArgumentsException(
                $"The spec has no chart panel, so {tool} has nothing to draw. Stat tiles and tables are text: call render_report for them.");
    }

    /// <summary>Runs the spec and draws its panels.</summary>
    public async Task<CallToolResult> RenderAsync(ReportSpec spec, ReportImageOptions options, CancellationToken cancellationToken)
    {
        var report = await renderer.RunAsync(spec, cancellationToken);
        return await DrawAsync(report, options, cancellationToken);
    }

    /// <summary>The summary text, then one image per drawn panel: the named one, else the first <see cref="MaxPanels"/> chart panels that were read.</summary>
    /// <exception cref="McpInvalidArgumentsException">The images together are larger than <see cref="MaxTotalBase64Bytes"/>.</exception>
    internal async Task<CallToolResult> DrawAsync(ReportResult report, ReportImageOptions options, CancellationToken cancellationToken)
    {
        var text = new StringBuilder(ReportRenderer.SummaryText(report));
        var charts = report.Panels
            .Where(p => options.PanelId is null ? IsChart(p.Primitive) : p.Id == options.PanelId)
            .ToList();
        var drawn = charts.Where(p => p.Status == ReportPanelStatus.Ok).Take(MaxPanels).ToList();

        var notDrawn = charts.Where(p => p.Status != ReportPanelStatus.Ok).Select(p => p.Id).ToList();
        if (notDrawn.Count > 0)
            text.Append('\n').Append($"No image of panel {string.Join(", ", notDrawn)}: its summary above says why it was not read.");
        var leftOut = charts.Where(p => p.Status == ReportPanelStatus.Ok).Skip(MaxPanels).Select(p => p.Id).ToList();
        if (leftOut.Count > 0)
            text.Append('\n').Append(
                $"Images are drawn for at most {MaxPanels} panels. Not drawn: {string.Join(", ", leftOut)}. Send panelId to draw one of them.");

        var images = new List<ContentBlock>();
        long total = 0;
        foreach (var panel in drawn)
        {
            byte[] png;
            try
            {
                png = await client.RenderAsync(
                    new ReportImageRequest(report with { Panels = [panel] }, panel.Id, options.Width, options.Height, options.Theme, options.DevicePixelRatio),
                    cancellationToken);
            }
            catch (ReportImageUnavailableException ex)
            {
                text.Append('\n').Append($"The images could not be drawn: {ex.Message} The report's text is above.");
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = text.ToString() }] };
            }

            total += Base64Length(png.Length);
            if (total > MaxTotalBase64Bytes)
                throw new McpInvalidArgumentsException(
                    $"The images are larger than {MaxTotalBase64Bytes / (1024.0 * 1024.0):0.#} MB, the most one answer carries. "
                    + "Send a smaller width or height, devicePixelRatio 1, or one panelId, and call again.");
            images.Add(ImageContentBlock.FromBytes(png, "image/png"));
            text.Append('\n').Append(string.Create(CultureInfo.InvariantCulture,
                $"Image {images.Count}: panel {panel.Id}, {options.Width} by {options.Height} pixels at ratio {options.DevicePixelRatio}, {options.Theme} theme."));
        }

        return new CallToolResult { Content = [new TextContentBlock { Text = text.ToString() }, .. images] };
    }

    /// <summary>The length of <paramref name="bytes"/> bytes written as base64.</summary>
    internal static long Base64Length(long bytes) => (bytes + 2) / 3 * 4;

    private static string Name(ReportPrimitive primitive) => primitive switch
    {
        ReportPrimitive.StatTiles => "stat_tiles",
        ReportPrimitive.Table => "table",
        _ => primitive.ToString(),
    };
}
