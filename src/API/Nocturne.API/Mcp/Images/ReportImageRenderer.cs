using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using ModelContextProtocol.Protocol;
using Nocturne.API.Mcp.Reports;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Mcp.Images;

/// <summary>
/// The size, theme and pixel ratio of a report's images, and the one panel to draw when named.
/// <see cref="Height"/> null leaves each panel's height to its primitive (a single stacked bar is short).
/// </summary>
internal sealed record ReportImageOptions(string? PanelId, int Width, int? Height, string Theme, int DevicePixelRatio);

/// <summary>
/// How many image calls a tenant may make: each one runs a report and draws up to
/// <see cref="ReportImageRenderer.MaxPanels"/> images, so it is held tighter than <c>/mcp</c> as a whole.
/// One per API instance.
/// </summary>
internal sealed class ReportImageRateLimit : IDisposable
{
    public const int PerMinute = 20;

    private readonly PartitionedRateLimiter<Guid> _limiter = PartitionedRateLimiter.Create<Guid, Guid>(tenant =>
        RateLimitPartition.GetFixedWindowLimiter(tenant, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    /// <summary>Takes one call's permit for the tenant; false when its minute is spent.</summary>
    public bool TryAcquire(Guid tenantId)
    {
        using var lease = _limiter.AttemptAcquire(tenantId);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// A report as images: the spec runs through <see cref="ReportRenderer"/> exactly as for
/// <c>render_report</c> (the caller's scopes, clamp and reader, and one audit row from the same read
/// log), then each chart panel is drawn as a PNG by <see cref="IReportImageClient"/>. The result is
/// the summary text followed by one image per panel, with no <c>structuredContent</c>: it is not a view.
/// </summary>
internal sealed class ReportImageRenderer(
    ReportRenderer renderer,
    IReportImageClient client,
    ReportImageRateLimit rateLimit,
    ITenantAccessor tenant)
{
    public const int DefaultWidth = 1200;
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

        return new ReportImageOptions(panelId, width ?? DefaultWidth, height, theme ?? "light", devicePixelRatio ?? 1);
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

    /// <summary>Runs the spec and draws its panels, within the tenant's image calls per minute.</summary>
    /// <exception cref="McpInvalidArgumentsException">The tenant has made <see cref="ReportImageRateLimit.PerMinute"/> calls this minute.</exception>
    public async Task<CallToolResult> RenderAsync(ReportSpec spec, ReportImageOptions options, CancellationToken cancellationToken)
    {
        if (!rateLimit.TryAcquire(tenant.TenantId))
            throw new McpInvalidArgumentsException(
                $"This account has asked for images {ReportImageRateLimit.PerMinute} times in the last minute, the most it may. Wait a minute and call again.");
        var report = await renderer.RunAsync(spec, cancellationToken);
        return await DrawAsync(report, options, cancellationToken);
    }

    /// <summary>
    /// The summary text, then one image per drawn panel: the named one, else the first
    /// <see cref="MaxPanels"/> chart panels that were read. Each image may use only what is left of
    /// <see cref="MaxTotalBase64Bytes"/>; a panel that does not fit, and those after it, are named instead.
    /// </summary>
    /// <exception cref="McpInvalidArgumentsException">Not even the first image fits.</exception>
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
        long used = 0;
        for (var i = 0; i < drawn.Count; i++)
        {
            var panel = drawn[i];
            byte[] png;
            try
            {
                png = await client.RenderAsync(
                    ReportImageRequest.For(report, panel, options), RawBytesFor(MaxTotalBase64Bytes - used), cancellationToken);
            }
            catch (ReportImageUnavailableException ex)
            {
                text.Append('\n').Append($"The images could not be drawn: {ex.Message} The report's text is above.");
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = text.ToString() }] };
            }
            catch (ReportImageTooLargeException)
            {
                var lower = options.DevicePixelRatio == 2 ? "a smaller width or height, or devicePixelRatio 1" : "a smaller width or height";
                if (images.Count == 0)
                    throw new McpInvalidArgumentsException(
                        $"The image of panel {panel.Id} is larger than {MaxTotalBase64Bytes / (1024.0 * 1024.0):0.#} MB, the most one answer "
                        + $"carries. Send {lower}, and call again.");
                text.Append('\n').Append(
                    $"Not drawn, because the images would pass {MaxTotalBase64Bytes / (1024.0 * 1024.0):0.#} MB, the most one answer carries: "
                    + $"{string.Join(", ", drawn.Skip(i).Select(p => p.Id))}. Send {lower}, or one panelId, to draw them.");
                break;
            }

            used += Base64Length(png.Length);
            images.Add(ImageContentBlock.FromBytes(png, "image/png"));
            var (width, height) = PngSize(png);
            text.Append('\n').Append(string.Create(CultureInfo.InvariantCulture,
                $"Image {images.Count}: panel {panel.Id}, {width} by {height} pixels, {options.Theme} theme."));
        }

        return new CallToolResult { Content = [new TextContentBlock { Text = text.ToString() }, .. images] };
    }

    /// <summary>The length of <paramref name="bytes"/> bytes written as base64.</summary>
    internal static long Base64Length(long bytes) => (bytes + 2) / 3 * 4;

    /// <summary>The most raw bytes whose base64 fits in <paramref name="base64"/> characters.</summary>
    internal static long RawBytesFor(long base64) => Math.Max(0, base64 / 4 * 3);

    /// <summary>A PNG's pixel size, from its header; zero when it has none.</summary>
    internal static (int Width, int Height) PngSize(byte[] png) => png.Length >= 24
        ? (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)))
        : (0, 0);

    private static string Name(ReportPrimitive primitive) => primitive switch
    {
        ReportPrimitive.StatTiles => "stat_tiles",
        ReportPrimitive.Table => "table",
        _ => primitive.ToString(),
    };
}
