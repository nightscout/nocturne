using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.TestDoubles;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

[Trait("Category", "Unit")]
public class OperatorWebhookLinkDeliveryTests
{
    private const string WebhookUrl = "https://operator.example/links";
    private const string Address = "a@example.test";
    private const string Link = "https://alpha.nocturne.example/auth/recovery?code=secret-code-value";

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(ct)));
            return await answer(request);
        }
    }

    private readonly ListLogger<OperatorWebhookLinkDelivery> _logger = new();

    private OperatorWebhookLinkDelivery Delivery(Handler handler, string? url = WebhookUrl)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(OperatorWebhookLinkDelivery.HttpClientName))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        return new OperatorWebhookLinkDelivery(
            factory.Object, Options.Create(new OperatorConfiguration { AccountLinkWebhookUrl = url }), _logger);
    }

    private static AccountLinkMessage Message(AccountLinkKind kind = AccountLinkKind.Recovery) => new(
        kind, Address, Link, new DateTime(2026, 11, 21, 10, 30, 0, DateTimeKind.Utc), "alpha", "Alpha household", "Rhys");

    private static Handler Answering(HttpStatusCode status) =>
        new(_ => Task.FromResult(new HttpResponseMessage(status)));

    [Fact]
    public async Task TheBody_IsExactlyTheDocumentedShape()
    {
        var handler = Answering(HttpStatusCode.Accepted);

        (await Delivery(handler).DeliverAsync(Message(AccountLinkKind.EmailConfirmation), default)).Should().BeTrue();

        var (uri, body) = handler.Requests.Should().ContainSingle().Subject;
        uri.Should().Be(new Uri(WebhookUrl));
        using var json = JsonDocument.Parse(body);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
            "kind", "email", "link", "expiresAt", "siteSlug", "siteDisplayName", "accountDisplayName");
        json.RootElement.GetProperty("kind").GetString().Should().Be("email_confirmation");
        json.RootElement.GetProperty("email").GetString().Should().Be(Address);
        json.RootElement.GetProperty("link").GetString().Should().Be(Link);
        json.RootElement.GetProperty("expiresAt").GetDateTime().Should().Be(new DateTime(2026, 11, 21, 10, 30, 0, DateTimeKind.Utc));
        json.RootElement.GetProperty("siteSlug").GetString().Should().Be("alpha");
        json.RootElement.GetProperty("siteDisplayName").GetString().Should().Be("Alpha household");
        json.RootElement.GetProperty("accountDisplayName").GetString().Should().Be("Rhys");
    }

    [Fact]
    public async Task ARecoveryLink_IsKindRecovery()
    {
        var handler = Answering(HttpStatusCode.OK);

        await Delivery(handler).DeliverAsync(Message(), default);

        JsonDocument.Parse(handler.Requests.Single().Body).RootElement.GetProperty("kind").GetString().Should().Be("recovery");
    }

    public static TheoryData<string> Failures => ["refused", "unreachable", "timed out"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailure_IsReportedAndLoggedWithoutTheAddressOrTheLink(string failure)
    {
        var handler = new Handler(_ => failure switch
        {
            "refused" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            "unreachable" => throw new HttpRequestException($"Connection refused {Address} {Link}", null, HttpStatusCode.BadGateway),
            _ => throw new TaskCanceledException($"Timed out sending {Link}"),
        });

        var delivered = await Delivery(handler).DeliverAsync(Message(), default);

        delivered.Should().BeFalse();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
        foreach (var (_, message, exception) in _logger.Entries)
        {
            var logged = message + exception;
            logged.Should().NotContain(Address).And.NotContain("secret-code-value");
        }
    }

    [Fact]
    public async Task AWebhookUrlHttpClientCannotCall_IsAFailedDeliveryNotAnException()
    {
        var handler = Answering(HttpStatusCode.OK);

        var delivered = await Delivery(handler, "/relative/path").DeliverAsync(Message(), default);

        delivered.Should().BeFalse();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("   ", true)]
    [InlineData("https://operator.example/links", true)]
    [InlineData("http://operator.internal:8080/links", true)]
    [InlineData("/relative/path", false)]
    [InlineData("ftp://operator.example/links", false)]
    [InlineData("not a url", false)]
    public void TheWebhookUrl_IsRefusedAtBindingUnlessUnsetOrAbsoluteHttp(string? url, bool valid)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Operator:AccountLinkWebhookUrl"] = url })
            .Build();
        using var provider = new ServiceCollection().AddAuthenticationAndIdentity(config).BuildServiceProvider();

        var reading = () => provider.GetRequiredService<IOptions<OperatorConfiguration>>().Value;

        if (valid)
            reading.Should().NotThrow();
        else
            reading.Should().Throw<OptionsValidationException>()
                .Which.Message.Should().Contain("Operator:AccountLinkWebhookUrl");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task WithNoWebhook_NothingIsConfiguredOrSent(string? url)
    {
        var handler = Answering(HttpStatusCode.OK);
        var delivery = Delivery(handler, url);

        delivery.IsConfigured.Should().BeFalse();
        (await delivery.DeliverAsync(Message(), default)).Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }
}
