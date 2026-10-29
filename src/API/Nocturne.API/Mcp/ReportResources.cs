using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Nocturne.API.Mcp;

[McpServerResourceType]
internal sealed class ReportResources
{
    public const string ReportUri = "ui://nocturne/report";

    private const string ClaudeSandboxSuffix = ".claudemcpcontent.com";

    [McpServerResource(UriTemplate = ReportUri, Name = "nocturne-report", Title = "Nocturne report", MimeType = McpApps.HtmlMimeType)]
    [Description("Interactive chart view for Nocturne's chart tools.")]
    public static ReadResourceResult GetReport(IHttpContextAccessor httpContextAccessor, ReportView view)
    {
        var html = view.GetHtml()
            ?? throw new McpProtocolException(
                "The report view was not built into this server.", McpErrorCode.InternalError);

        return new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = ReportUri,
                    MimeType = McpApps.HtmlMimeType,
                    Text = html,
                    Meta = UiMeta(httpContextAccessor.HttpContext!.Request),
                },
            ],
        };
    }

    /// <summary>
    /// The view's <c>_meta</c>. No CSP domains: the view is one inlined file that fetches nothing,
    /// so the host's default CSP fits it.
    /// </summary>
    internal static JsonObject UiMeta(HttpRequest request) => new()
    {
        ["ui"] = JsonSerializer.SerializeToNode(
            new McpUiResourceMeta
            {
                PrefersBorder = true,
                Domain = ClaudeSandboxDomain(McpAccessGateMiddleware.ResourceUrl(request)),
            },
            McpApps.SerializerOptions),
    };

    /// <summary>
    /// The sandbox origin Claude serves the view from, derived from the MCP URL the user entered,
    /// which on Nocturne is per tenant host. Claude refuses a view whose declared domain differs.
    /// </summary>
    internal static string ClaudeSandboxDomain(string serverUrl) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(serverUrl)))[..32]
        + ClaudeSandboxSuffix;

    /// <summary>
    /// Gives the listed report resource the same per-host <c>_meta</c> as its contents. The listed
    /// <see cref="Resource"/> is the server's shared instance, so it is replaced by a copy rather
    /// than written to.
    /// </summary>
    internal static McpRequestHandler<ListResourcesRequestParams, ListResourcesResult> WithUiMeta(
        McpRequestHandler<ListResourcesRequestParams, ListResourcesResult> next) =>
        async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            var httpContext = request.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
            if (httpContext is null)
                return result;

            for (var i = 0; i < result.Resources.Count; i++)
            {
                var listed = result.Resources[i];
                if (listed.Uri != ReportUri)
                    continue;

                result.Resources[i] = new Resource
                {
                    Name = listed.Name,
                    Title = listed.Title,
                    Uri = listed.Uri,
                    Description = listed.Description,
                    MimeType = listed.MimeType,
                    Annotations = listed.Annotations,
                    Size = listed.Size,
                    Icons = listed.Icons,
                    Meta = UiMeta(httpContext.Request),
                };
            }

            return result;
        };
}
