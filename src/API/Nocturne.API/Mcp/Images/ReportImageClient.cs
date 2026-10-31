using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.API.Authorization;
using Nocturne.API.Mcp.Reports;
using Nocturne.Core.Constants;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Mcp.Images;

/// <summary>
/// One panel to draw and what its drawing needs from the report: the glucose unit, the tenant's zone,
/// locale and clock, and the colour mode. Nothing else of the report (its summary, spec, links, other
/// panels) leaves the API. <see cref="Height"/> null lets the web app pick the primitive's height.
/// </summary>
internal sealed record ReportImageRequest(
    ReportPanelResult Panel,
    string Units,
    string? TimeZone,
    string GlucoseColorMode,
    string? Locale,
    bool? Hour12,
    int Width,
    int? Height,
    string Theme,
    int DevicePixelRatio)
{
    /// <summary>The request for one panel; the panel's own texts and link stay here too, since the image draws none of them.</summary>
    public static ReportImageRequest For(ReportResult report, ReportPanelResult panel, ReportImageOptions options) => new(
        panel with { Summary = "", Sentence = null, Facts = [], Link = null }, report.Units, report.TimeZone, report.GlucoseColorMode, report.Locale, report.Hour12,
        options.Width, options.Height, options.Theme, options.DevicePixelRatio);
}

/// <summary>Draws a report panel as a PNG. The web app does it (<see cref="WebReportImageClient"/>); tests supply a fake.</summary>
internal interface IReportImageClient
{
    /// <summary>The PNG's bytes, at most <paramref name="maxBytes"/> of them.</summary>
    /// <exception cref="ReportImageTooLargeException">The PNG is larger than <paramref name="maxBytes"/>.</exception>
    /// <exception cref="ReportImageUnavailableException">The image could not be drawn; the message says why, for the caller.</exception>
    Task<byte[]> RenderAsync(ReportImageRequest request, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>An image that could not be drawn. The message is written for the assistant and holds no secret.</summary>
internal sealed class ReportImageUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A PNG larger than the room left in the answer; it was not read past that room.</summary>
internal sealed class ReportImageTooLargeException() : Exception("The image is larger than the room left in the answer.");

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

    /// <summary>The longest one image may take, from sending the request to the last byte of the PNG.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary><see cref="Timeout"/>, shorter in a test.</summary>
    internal TimeSpan Deadline { get; init; } = Timeout;

    /// <summary>
    /// The most images this API instance asks for at once, whatever the tenant: the web app draws one at a
    /// time per worker, so more only queue there.
    /// </summary>
    internal static readonly SemaphoreSlim InFlight = new(2, 2);

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public async Task<byte[]> RenderAsync(ReportImageRequest request, long maxBytes, CancellationToken cancellationToken)
    {
        var webUrl = configuration["WEB_URL"];
        if (string.IsNullOrEmpty(webUrl))
            throw new ReportImageUnavailableException("this server does not know the address of the web app that draws images.");
        var digest = InstanceKeyDigest.Resolve(configuration);
        if (string.IsNullOrEmpty(digest))
            throw new ReportImageUnavailableException("this server has no instance key to identify itself to the web app that draws images.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        try
        {
            await InFlight.WaitAsync(deadline.Token);
            try
            {
                return await SendAsync(request, webUrl, digest, maxBytes, deadline.Token);
            }
            finally
            {
                InFlight.Release();
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The web app did not draw report panel {PanelId} in time", request.Panel.Id);
            throw new ReportImageUnavailableException(string.Create(CultureInfo.InvariantCulture, $"the web app that draws images did not answer within {Deadline.TotalSeconds:0.#} seconds."), ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogWarning(ex, "The web app could not be reached to draw report panel {PanelId}", request.Panel.Id);
            throw new ReportImageUnavailableException("the web app that draws images did not answer.", ex);
        }
    }

    private async Task<byte[]> SendAsync(ReportImageRequest request, string webUrl, string digest, long maxBytes, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{webUrl.TrimEnd('/')}/api/internal/report-image")
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
            {
                panel = request.Panel,
                units = request.Units,
                timeZone = request.TimeZone,
                glucoseColorMode = request.GlucoseColorMode,
                locale = request.Locale,
                hour12 = request.Hour12,
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
        if (request.Locale is { } locale)
            message.Headers.AcceptLanguage.ParseAdd(locale);

        using var response = await httpClientFactory.CreateClient(HttpClientName)
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            throw new ReportImageUnavailableException("the image renderer is busy. Try again.");
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
            throw new ReportImageUnavailableException($"the web app refused the panel: {await ErrorOf(response, cancellationToken)}");
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("The web app answered {Status} to report panel {PanelId}", (int)response.StatusCode, request.Panel.Id);
            throw new ReportImageUnavailableException($"the web app that draws images answered with an error ({(int)response.StatusCode}).");
        }

        var png = await ReadCappedAsync(response, maxBytes, cancellationToken);
        if (!png.AsSpan().StartsWith(PngSignature))
            throw new ReportImageUnavailableException("the web app that draws images did not answer with a PNG.");
        return png;
    }

    /// <summary>The body, reading no more than one byte past <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new ReportImageTooLargeException();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new ReportImageTooLargeException();
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
