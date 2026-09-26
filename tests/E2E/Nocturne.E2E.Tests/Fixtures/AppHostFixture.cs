using Aspire.Hosting;
using Nocturne.Core.Constants;
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
    public string WebBaseUrl { get; private set; } = default!;
    public string WebHost { get; private set; } = default!;

    /// <summary>
    /// The host a tenant is addressed by, as the app knows it: the app host
    /// derives BASE_DOMAIN from the gateway, so a request that reaches the web
    /// server on its own port still has to carry this name or the app answers
    /// as though no tenant existed.
    /// </summary>
    public string TenantHostSuffix { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        NightscoutMock = WireMockServer.Start();
        WireMockNightscoutFixtures.Load(NightscoutMock);

        Console.Error.WriteLine("[E2E] Creating AppHost builder...");
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Nocturne_Aspire_Host>();

        // The AppHost's secret parameters have no default: outside a developer's
        // machine there are no user secrets to resolve them from, and a test run
        // cannot answer the dashboard prompt, so every resource that depends on
        // one waits forever and no container is ever created. Supply throwaway
        // values — the stack is torn down with the fixture.
        foreach (var parameter in new[]
                 {
                     ServiceNames.Parameters.PostgresPassword,
                     ServiceNames.Parameters.PostgresMigratorPassword,
                     ServiceNames.Parameters.PostgresAppPassword,
                     ServiceNames.Parameters.PostgresWebPassword,
                     ServiceNames.Parameters.InstanceKey,
                 })
        {
            builder.Configuration[$"Parameters:{parameter}"] =
                "e2e-" + Guid.NewGuid().ToString("N");
        }

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

        // Addressed per resource rather than through the gateway: under the test
        // harness the gateway is published without a usable TLS certificate, and
        // its plain listener closes the connection mid-response. What these tests
        // assert lives in the API and the web server, so they talk to those.
        var apiEndpoint = App.GetEndpoint("nocturne-api", "http");
        GatewayBaseUrl = apiEndpoint.ToString().TrimEnd('/');
        GatewayHost = apiEndpoint.Host + ":" + apiEndpoint.Port;

        var webEndpoint = App.GetEndpoint("nocturne-web", "http");
        WebBaseUrl = webEndpoint.ToString().TrimEnd('/');
        WebHost = webEndpoint.Host + ":" + webEndpoint.Port;

        TenantHostSuffix = "nocturne.localhost:" + App.GetEndpoint("gateway", "https").Port;

        Console.Error.WriteLine(
            $"[E2E] API at {GatewayBaseUrl}, web at {WebBaseUrl}, tenants at *.{TenantHostSuffix}");
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
