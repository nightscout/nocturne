using Nocturne.Connectors.Eversense.Services;

namespace Nocturne.API.Authorization;

public static class DevOnlyEversenseMock
{
    public const string EnableVariable = "NOCTURNE_EVERSENSE_MOCK";

    public static void AddDevOnlyEversenseMock(
        this IServiceCollection services, IHostEnvironment environment, IConfiguration configuration)
    {
        if (!DevOnlyEndpoints.AreEnabled(environment, configuration) ||
            !string.Equals(configuration[EnableVariable]?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return;

        services.AddHttpClient<EversenseConnectorService>().AddHttpMessageHandler(() => new EversenseMockHandler());
        services.AddHttpClient<EversenseAuthTokenProvider>().AddHttpMessageHandler(() => new EversenseMockHandler());
    }

    private sealed class EversenseMockHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Scheme != "https" || !uri.IsDefaultPort ||
                !uri.Host.EndsWith(".eversensedms.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected Eversense mock destination.");

            // Only the explicitly opted-in disposable stack can send credentials to its fake vendor.
            request.RequestUri = new Uri("http://mocks:8080/eversense/" + uri.Host + uri.PathAndQuery);
            try { return await base.SendAsync(request, ct); }
            finally { request.RequestUri = uri; }
        }
    }
}
