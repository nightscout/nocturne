namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The limit every outbound alert delivery call shares. Delivery is awaited inside the evaluation
/// pass, so a target that never answers holds the pass, and past the owed lease a second pass can
/// send the same opening again. There is no retry: a second POST of an alert is not idempotent.
/// </summary>
internal static class AlertDeliveryClient
{
    /// <summary>How long one delivery call may take before it counts as failed.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static IHttpClientBuilder AddAlertDeliveryClient(this IServiceCollection services, string name) =>
        services.AddHttpClient(name, client => client.Timeout = Timeout);
}
