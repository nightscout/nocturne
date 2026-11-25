namespace Nocturne.Core.Constants;

/// <summary>What an outbound request tells the telemetry that records it.</summary>
public static class OutboundTelemetry
{
    /// <summary>
    /// The URL a span records for the request in place of its own, set by a client whose request
    /// path or query can be a credential. A span for a request without it records the request's URL.
    /// </summary>
    public static readonly HttpRequestOptionsKey<string> DisplayUrl = new("Nocturne.OutboundTelemetry.DisplayUrl");
}
