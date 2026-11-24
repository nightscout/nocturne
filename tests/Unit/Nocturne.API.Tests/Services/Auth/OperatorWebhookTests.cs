using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

[Trait("Category", "Unit")]
public class OperatorWebhookTests
{
    private const string Url = "https://operator.example/hook";

    private sealed record Answer(bool IsValid);

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(ct);
    }

    private static Handler Silent(TaskCompletionSource? arrived = null) => new(async ct =>
    {
        arrived?.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return new HttpResponseMessage(HttpStatusCode.OK);
    });

    private static Handler Answering(HttpStatusCode status, string body = "") => new(_ =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    private static HttpClient Client(Handler handler, TimeSpan? timeout = null) =>
        new(handler) { Timeout = timeout ?? OperatorWebhook.Timeout };

    [Fact]
    public async Task AWebhookThatDoesNotAnswerInTime_IsTimedOutNotThrown()
    {
        using var client = Client(Silent(), TimeSpan.FromMilliseconds(50));

        var result = await OperatorWebhook.PostAsync<Answer>(client, Url, new { slug = "a" }, CancellationToken.None);

        result.Outcome.Should().Be(OperatorWebhookOutcome.TimedOut);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task ACallerThatCancels_IsNotMistakenForATimeout()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client(Silent(arrived));
        using var cts = new CancellationTokenSource();

        var call = OperatorWebhook.PostAsync<Answer>(client, Url, new { slug = "a" }, cts.Token);
        await arrived.Task;
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ACallerThatCancelsABodylessPost_IsNotMistakenForATimeout()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client(Silent(arrived));
        using var cts = new CancellationTokenSource();

        var call = OperatorWebhook.PostAsync(client, Url, new { slug = "a" }, cts.Token);
        await arrived.Task;
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AnAnswer_IsReadAsTheRequestedType()
    {
        using var client = Client(Answering(HttpStatusCode.OK, """{"isValid":false}"""));

        var result = await OperatorWebhook.PostAsync<Answer>(client, Url, new { slug = "a" }, CancellationToken.None);

        result.Outcome.Should().Be(OperatorWebhookOutcome.Answered);
        result.Value.Should().Be(new Answer(false));
    }

    [Fact]
    public async Task ANonSuccessStatus_IsRefusedWithItsStatus()
    {
        using var client = Client(Answering(HttpStatusCode.ServiceUnavailable, """{"isValid":false}"""));

        var result = await OperatorWebhook.PostAsync<Answer>(client, Url, new { slug = "a" }, CancellationToken.None);

        result.Outcome.Should().Be(OperatorWebhookOutcome.Refused);
        result.StatusCode.Should().Be(503);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task ABodyThatIsNotTheExpectedJson_IsUnreadable()
    {
        using var client = Client(Answering(HttpStatusCode.OK, "not json"));

        var result = await OperatorWebhook.PostAsync<Answer>(client, Url, new { slug = "a" }, CancellationToken.None);

        result.Outcome.Should().Be(OperatorWebhookOutcome.Unreadable);
    }

    [Fact]
    public async Task ATransportError_IsUnreachable()
    {
        using var client = Client(new Handler(_ => throw new HttpRequestException("down", null, HttpStatusCode.BadGateway)));

        var result = await OperatorWebhook.PostAsync<Answer>(client, Url, new { slug = "a" }, CancellationToken.None);

        result.Outcome.Should().Be(OperatorWebhookOutcome.Unreachable);
    }

    [Theory]
    [InlineData(OperatorWebhook.AccountLinkClient)]
    [InlineData(OperatorWebhook.SlugValidationClient)]
    [InlineData(OperatorWebhook.UsernameValidationClient)]
    public void EveryOperatorWebhookClient_IsRegisteredWithTheShortTimeout(string name)
    {
        var config = new ConfigurationBuilder().Build();
        using var provider = new ServiceCollection().AddAuthenticationAndIdentity(config).BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        client.Timeout.Should().Be(OperatorWebhook.Timeout);
    }

    [Theory]
    [InlineData(OperatorWebhook.AccountLinkClient)]
    [InlineData(OperatorWebhook.SlugValidationClient)]
    [InlineData(OperatorWebhook.UsernameValidationClient)]
    public async Task EveryOperatorWebhookClient_DoesNotFollowARedirect(string name)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var requests = 0;
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Increment(ref requests);
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = $"http://127.0.0.1:{port}/elsewhere";
                context.Response.Close();
            }
        });
        var config = new ConfigurationBuilder().Build();
        using var provider = new ServiceCollection().AddAuthenticationAndIdentity(config).BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        var result = await OperatorWebhook.PostAsync(client, $"http://127.0.0.1:{port}/hook", new { slug = "a" }, CancellationToken.None);

        listener.Stop();
        await serving;
        result.Outcome.Should().Be(OperatorWebhookOutcome.Refused);
        result.StatusCode.Should().Be(302);
        requests.Should().Be(1);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
