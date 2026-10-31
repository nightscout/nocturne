using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.API.Authorization;
using Nocturne.API.Mcp.Reports;
using Nocturne.Core.Constants;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Mcp.Images;

/// <summary>One panel of a report to draw, at a size, in a theme and pixel ratio.</summary>
/// <param name="Result">The report holding only the panel to draw.</param>
internal sealed record ReportImageRequest(ReportResult Result, string PanelId, int Width, int Height, string Theme, int DevicePixelRatio);

/// <summary>Draws a report panel as a PNG. The web app does it (<see cref="WebReportImageClient"/>); tests supply a fake.</summary>
internal interface IReportImageClient
{
    /// <summary>The PNG's bytes.</summary>
    /// <exception cref="ReportImageUnavailableException">The image could not be drawn; the message says why, for the caller.</exception>
    Task<byte[]> RenderAsync(ReportImageRequest request, CancellationToken cancellationToken);
}

/// <summary>An image that could not be drawn. The message is written for the assistant and holds no secret.</summary>
internal sealed class ReportImageUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Asks the web app's <c>POST /api/internal/report-image</c> for a panel's PNG, the way
/// <see cref="Services.Alerts.Providers.ChatBotProvider"/> posts a bot dispatch: to <c>WEB_URL</c>,
/// the web app's address inside the deployment, with the instance key as the service credential.
/// The route is reachable from the internet through the gateway, which is why it is guarded.
/// </summary>
internal sealed class WebReportImageClient(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IOptions<MvcJsonOptions> jsonOptions,
    ILogger<WebReportImageClient> logger) : IReportImageClient
{
    public const string HttpClientName = "ReportImage";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The largest answer read: a 2400 by 1600 panel at ratio 2 is far below it.</summary>
    internal const int MaxResponseBytes = 16 * 1024 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public async Task<byte[]> RenderAsync(ReportImageRequest request, CancellationToken cancellationToken)
    {
        var webUrl = configuration["WEB_URL"];
        if (string.IsNullOrEmpty(webUrl))
            throw new ReportImageUnavailableException("this server does not know the address of the web app that draws images.");
        var digest = InstanceKeyDigest.Resolve(configuration);
        if (string.IsNullOrEmpty(digest))
            throw new ReportImageUnavailableException("this server has no instance key to identify itself to the web app that draws images.");

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{webUrl.TrimEnd('/')}/api/internal/report-image")
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
            {
                result = request.Result,
                panelId = request.PanelId,
                width = request.Width,
                height = request.Height,
                theme = request.Theme,
                devicePixelRatio = request.DevicePixelRatio,
            }, jsonOptions.Value.JsonSerializerOptions))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        };
        message.Headers.Add(ServiceNames.Headers.InstanceKey, digest);
        message.Headers.Add(ServiceNames.Headers.InstanceService, ServiceNames.NocturneApi);

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The web app did not draw report panel {PanelId} in time", request.PanelId);
            throw new ReportImageUnavailableException($"the web app that draws images did not answer within {Timeout.TotalSeconds:0} seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "The web app could not be reached to draw report panel {PanelId}", request.PanelId);
            throw new ReportImageUnavailableException("the web app that draws images did not answer.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.BadRequest)
                throw new ReportImageUnavailableException($"the web app refused the panel: {await ErrorOf(response, cancellationToken)}");
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The web app answered {Status} to report panel {PanelId}", (int)response.StatusCode, request.PanelId);
                throw new ReportImageUnavailableException($"the web app that draws images answered with an error ({(int)response.StatusCode}).");
            }

            var png = await ReadCappedAsync(response, cancellationToken);
            if (png is null || !png.AsSpan().StartsWith(PngSignature))
                throw new ReportImageUnavailableException("the web app that draws images did not answer with a PNG.");
            return png;
        }
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>The web app's <c>{"error": "..."}</c>, at most a line of it.</summary>
    private static async Task<string> ErrorOf(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.GetProperty("error").GetString() ?? "";
            return error.Length > 300 ? error[..300] : error;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "no reason given.";
        }
    }
}
