using System.Net;
using Nocturne.Services.SelfHostSetup;

var builder = WebApplication.CreateSlimBuilder(args);

var configDirectory = builder.Configuration["SETUP_CONFIG_DIR"] ?? "/config";
var claimCode = builder.Configuration["SETUP_CLAIM_CODE"]
    ?? throw new InvalidOperationException("SETUP_CLAIM_CODE is not set: the first-boot script passes the instance ID.");

builder.Services.AddHttpClient<DuckDnsClient>(http => http.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton(new ClaimGuard(claimCode));
builder.Services.AddSingleton<IDnsResolver, SystemDnsResolver>();
builder.Services.AddSingleton<DnsProbe>();
builder.Services.AddSingleton(sp => new SetupOptions(
    configDirectory,
    PublicAddress.Resolve(builder.Configuration["PUBLIC_IP"], sp.GetRequiredService<IHttpClientFactory>())));
builder.Services.AddSingleton<SetupCoordinator>();

var app = builder.Build();
var coordinator = app.Services.GetRequiredService<SetupCoordinator>();
var publicIp = app.Services.GetRequiredService<SetupOptions>().PublicIp;

if (coordinator.IsComplete)
{
    app.Logger.LogInformation("Setup already complete; exiting so the stack can start.");
    return;
}

app.MapGet("/health", () => Results.Ok());
app.MapGet("/", () => SetupPage.Render(publicIp, outcome: null));

app.MapPost("/duckdns", async (HttpRequest request, IHostApplicationLifetime lifetime, CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    var outcome = await coordinator.UseDuckDnsAsync(form["claim"], form["name"], form["token"], ct);
    return Respond(outcome, lifetime);
});

app.MapPost("/domain", async (HttpRequest request, IHostApplicationLifetime lifetime, CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    var outcome = await coordinator.UseCustomDomainAsync(form["claim"], form["domain"], ct);
    return Respond(outcome, lifetime);
});

app.Run();

IResult Respond(SetupOutcome outcome, IHostApplicationLifetime lifetime)
{
    // Exiting hands port 80 to Caddy; the delay lets this response reach the browser first.
    if (outcome is SetupOutcome.Completed)
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => lifetime.StopApplication());

    return SetupPage.Render(publicIp, outcome);
}

static class PublicAddress
{
    /// <summary>
    /// The first-boot script reads the address from the cloud's metadata service; a provider
    /// without one falls back to asking what address the internet sees.
    /// </summary>
    public static IPAddress Resolve(string? configured, IHttpClientFactory httpClients)
    {
        if (IPAddress.TryParse(configured, out var address))
            return address;

        var seen = httpClients.CreateClient().GetStringAsync("https://checkip.amazonaws.com").GetAwaiter().GetResult();
        return IPAddress.Parse(seen.Trim());
    }
}
