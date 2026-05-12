using Aspire.Hosting;
using Aspire.Hosting.Testing;
using WireMock.Server;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.E2E.Tests.Fixtures;

public sealed class AppHostFixture : IAsyncLifetime
{
    public DistributedApplication App { get; private set; } = default!;
    public WireMockServer NightscoutMock { get; private set; } = default!;
    public string GatewayBaseUrl { get; private set; } = default!;
    public string GatewayHost { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        NightscoutMock = WireMockServer.Start();
        WireMockNightscoutFixtures.Load(NightscoutMock);

        Console.Error.WriteLine("[E2E] Creating AppHost builder...");
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Nocturne_Aspire_Host>();

        builder.Configuration["Aspire:OptionalServices:DemoService:Enabled"] = "false";
        builder.Configuration["Aspire:OptionalServices:Scalar:Enabled"] = "false";
        builder.Configuration["Aspire:OptionalServices:AspireDashboard:Enabled"] = "false";

        Console.Error.WriteLine("[E2E] Building AppHost...");
        App = await builder.BuildAsync();

        Console.Error.WriteLine("[E2E] Starting AppHost...");
        await App.StartAsync();

        Console.Error.WriteLine("[E2E] Waiting for gateway to become healthy (up to 5 min)...");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await App.ResourceNotifications
            .WaitForResourceHealthyAsync("gateway", cts.Token);

        var gatewayEndpoint = App.GetEndpoint("gateway", "https");
        GatewayBaseUrl = gatewayEndpoint.ToString().TrimEnd('/');
        GatewayHost = gatewayEndpoint.Host + ":" + gatewayEndpoint.Port;
        Console.Error.WriteLine($"[E2E] Gateway ready at {GatewayBaseUrl}");
    }

    public HttpClient CreateGatewayClient(string? tenantSlug = null, string? bearerToken = null)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri(GatewayBaseUrl) };

        if (tenantSlug is not null)
            client.DefaultRequestHeaders.Host = $"{tenantSlug}.{GatewayHost}";
        if (bearerToken is not null)
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);

        return client;
    }

    public async Task DisposeAsync()
    {
        await App.DisposeAsync();
        NightscoutMock.Dispose();
    }
}
