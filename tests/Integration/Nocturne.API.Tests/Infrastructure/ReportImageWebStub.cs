using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// The web app's <c>POST /api/internal/report-image</c> as the API sees it: the API under test is
/// pointed at this through <c>WEB_URL</c>. It checks the instance key the way the web route does,
/// keeps what it was sent, and answers a small PNG, or drops the connection to act as a web app that
/// is down. The web route's own rendering is tested in the web app.
/// </summary>
public sealed class ReportImageWebStub : IAsyncDisposable
{
    /// <summary>A valid 1 by 1 PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private readonly WebApplication _app;
    private readonly List<JsonElement> _received = [];

    private ReportImageWebStub(WebApplication app) => _app = app;

    /// <summary>When true, every request's connection is dropped with no answer.</summary>
    public bool Down { get; set; }

    public string Url { get; private set; } = string.Empty;

    /// <summary>The bodies of the requests that presented the instance key, oldest first.</summary>
    public IReadOnlyList<JsonElement> Received
    {
        get
        {
            lock (_received)
                return [.. _received];
        }
    }

    public static async Task<ReportImageWebStub> StartAsync(string instanceKey)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceKey)));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var stub = new ReportImageWebStub(app);

        app.MapPost("/api/internal/report-image", async (HttpContext context) =>
        {
            if (stub.Down)
            {
                context.Abort();
                return;
            }
            if (context.Request.Headers["X-Instance-Key"] != digest || context.Request.Headers["X-Instance-Service"] != "nocturne-api")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body);
            lock (stub._received)
                stub._received.Add(body);
            context.Response.ContentType = "image/png";
            await context.Response.Body.WriteAsync(Png);
        });

        await app.StartAsync();
        stub.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return stub;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
