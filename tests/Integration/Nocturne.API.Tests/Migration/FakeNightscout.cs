using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// A Nightscout on a loopback port that answers each GET path of a route map with the JSON its
/// delegate returns, and answers <c>/api/v1/status</c> as a v15 Nightscout unless the map sets it.
/// </summary>
internal sealed class FakeNightscout : IAsyncDisposable
{
    private const string StatusPath = "/api/v1/status";
    private const string StatusBody = """{"status":"ok","name":"Nightscout","version":"15.0.0","apiEnabled":true}""";

    private readonly WebApplication _app;

    private FakeNightscout(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public static Func<HttpContext, Task<string>> Body(string json) => _ => Task.FromResult(json);

    public static Func<HttpContext, Task<string>> Body(Func<string> json) => _ => Task.FromResult(json());

    public static async Task<FakeNightscout> StartAsync(IReadOnlyDictionary<string, Func<HttpContext, Task<string>>> routes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();

        foreach (var (path, body) in routes)
            app.MapGet(path, async context =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(await body(context), context.RequestAborted);
            });
        if (!routes.ContainsKey(StatusPath))
            app.MapGet(StatusPath, () => Results.Content(StatusBody, "application/json"));

        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new FakeNightscout(app, url);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
