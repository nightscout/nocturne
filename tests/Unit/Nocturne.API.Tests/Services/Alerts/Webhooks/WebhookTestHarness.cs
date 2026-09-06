using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts.Webhooks;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Shared.Services;

namespace Nocturne.API.Tests.Services.Alerts.Webhooks;

/// <summary>
/// Shared rig for the webhook signing tests: a real <see cref="SecretEncryptionService"/> so a
/// secret makes the same round trip it makes in production, and an HTTP client that captures what
/// a receiver would see instead of sending it.
/// </summary>
internal static class WebhookTestHarness
{
    /// <summary>
    /// An IP literal skips DNS in <c>OutboundDestination</c>, and the documentation range
    /// classifies as publicly routable, so the send reaches the capturing handler.
    /// </summary>
    public const string Url = "https://203.0.113.10/hook";

    public static ISecretEncryptionService Encryption() => new SecretEncryptionService(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ServiceNames.ConfigKeys.InstanceKey] = "test-instance-key",
            })
            .Build(),
        NullLogger<SecretEncryptionService>.Instance);

    public static WebhookRequestSender Sender(CapturingWebhookHandler handler)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(f => f.CreateClient(WebhookRequestSender.HttpClientName))
            .Returns(new HttpClient(handler));

        return new WebhookRequestSender(
            httpClientFactory.Object, NullLogger<WebhookRequestSender>.Instance);
    }

    internal sealed class CapturingWebhookHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        public Dictionary<string, string> CapturedHeaders { get; } = [];

        public string? Signature =>
            CapturedHeaders.TryGetValue("X-Nocturne-Signature", out var signature) ? signature : null;

        public long Timestamp => long.Parse(CapturedHeaders["X-Nocturne-Timestamp"]);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Read inside the handler: the sender disposes its StringContent once the send returns.
            CapturedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            foreach (var (name, values) in request.Content.Headers)
            {
                CapturedHeaders[name] = string.Join(',', values);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
