using Nocturne.Connectors.GoogleHealth.Services;

namespace Nocturne.API.Authorization;

public static class DevOnlyGoogleHealthMock
{
    public const string EnableVariable = "NOCTURNE_GOOGLE_HEALTH_MOCK";

    public static void AddDevOnlyGoogleHealthMock(
        this IServiceCollection services, IHostEnvironment environment, IConfiguration configuration)
    {
        if (!DevOnlyEndpoints.AreEnabled(environment, configuration) ||
            !string.Equals(configuration[EnableVariable]?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return;

        services.AddHttpClient<GoogleHealthClient>().AddHttpMessageHandler(() => new GoogleHealthMockHandler());
        services.AddHttpClient<GoogleHealthAuthTokenProvider>().AddHttpMessageHandler(() => new GoogleHealthMockHandler());
    }

    private sealed class GoogleHealthMockHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Scheme != "https" || !uri.IsDefaultPort ||
                uri.Host is not ("oauth2.googleapis.com" or "openidconnect.googleapis.com" or "health.googleapis.com"))
                throw new InvalidOperationException("Unexpected Google Health mock destination.");

            // Only the explicitly opted-in disposable stack can send credentials to its fake vendor.
            request.RequestUri = new Uri("http://mocks:8080/googlehealth/" + uri.Host + uri.PathAndQuery);
            return base.SendAsync(request, ct);
        }
    }
}
