using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Tests.Services.Alerts.Webhooks;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A delivery target that does not answer is a failed delivery after <see cref="AlertDeliveryClient.Timeout"/>,
/// never a wait for HttpClient's 100 second default and never a cancellation: only the caller's own
/// token cancels.
/// </summary>
[Trait("Category", "Unit")]
public class AlertDeliveryTimeoutTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    /// <summary>Short enough to keep the suite quick; the handler never completes, so only this limit ends the call.</summary>
    private static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(50);

    [Theory]
    [InlineData(WebhookRequestSenderName)]
    [InlineData(ChatBotProvider.HttpClientName)]
    public void Every_alert_delivery_client_carries_the_shared_timeout(string clientName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlertingAndMonitoring(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName)
            .Timeout.Should().Be(AlertDeliveryClient.Timeout);
    }

    [Fact]
    public async Task Webhook_sender_reports_a_target_that_does_not_answer_as_failed()
    {
        var sender = WebhookTestHarness.Sender(new NeverAnswers(), ShortLimit);

        var failures = await sender.SendAsync([WebhookTestHarness.Url], "{}", null, CancellationToken.None);

        failures.Should().Equal(WebhookTestHarness.Url);
    }

    [Fact]
    public async Task Webhook_sender_lets_the_callers_cancellation_through()
    {
        var handler = new NeverAnswers();
        var sender = WebhookTestHarness.Sender(handler);
        using var cts = new CancellationTokenSource();

        var send = sender.SendAsync([WebhookTestHarness.Url], "{}", null, cts.Token);
        await handler.Started;
        await cts.CancelAsync();

        await send.Awaiting(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Webhook_sender_records_a_transport_failure_and_still_sends_the_remaining_urls()
    {
        const string Refused = "https://203.0.113.20/hook";
        const string Answers = "https://203.0.113.21/hook";
        var handler = new Scripted(refuse: Refused);
        var sender = WebhookTestHarness.Sender(handler);

        var failures = await sender.SendAsync([Refused, Answers], "{}", null, CancellationToken.None);

        failures.Should().Equal(Refused);
        handler.Requested.Should().BeEquivalentTo([Refused, Answers]);
    }

    [Fact]
    public async Task Webhook_sender_ends_within_one_timeout_when_no_url_answers()
    {
        string[] urls = ["https://203.0.113.30/hook", "https://203.0.113.31/hook", "https://203.0.113.32/hook"];
        var clock = new FakeTimeProvider();
        var handler = new Scripted(hang: urls);
        var sender = WebhookTestHarness.Sender(handler, timeProvider: clock);

        var send = sender.SendAsync(urls, "{}", null, CancellationToken.None);
        await handler.AllHanging.WaitAsync(Eventually.DefaultPatience);
        clock.Advance(AlertDeliveryClient.Timeout);

        (await send.WaitAsync(Eventually.DefaultPatience)).Should().Equal(urls);
    }

    [Fact]
    public async Task Webhook_sender_reports_each_failed_url_in_order_and_not_the_one_that_answered()
    {
        const string Refused = "https://203.0.113.40/hook";
        const string Answers = "https://203.0.113.41/hook";
        const string Silent = "https://203.0.113.42/hook";
        var clock = new FakeTimeProvider();
        var handler = new Scripted(refuse: Refused, hang: [Silent]);
        var sender = WebhookTestHarness.Sender(handler, timeProvider: clock);

        var send = sender.SendAsync([Silent, Answers, Refused], "{}", null, CancellationToken.None);
        await handler.AllHanging.WaitAsync(Eventually.DefaultPatience);
        clock.Advance(AlertDeliveryClient.Timeout);

        (await send.WaitAsync(Eventually.DefaultPatience)).Should().Equal(Silent, Refused);
        handler.Requested.Should().BeEquivalentTo([Silent, Answers, Refused]);
    }

    [Fact]
    public async Task Webhook_sender_bounds_the_destination_check_by_the_delivery_timeout()
    {
        const string Unresolvable = "https://slow-dns.example/hook";
        const string Answers = "https://203.0.113.21/hook";
        var clock = new FakeTimeProvider();
        var resolverStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Scripted();
        var sender = WebhookTestHarness.Sender(
            handler,
            timeProvider: clock,
            resolver: async (_, ct) =>
            {
                resolverStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return [];
            });

        var send = sender.SendAsync([Unresolvable, Answers], "{}", null, CancellationToken.None);
        await resolverStarted.Task;
        send.IsCompleted.Should().BeFalse("the resolver has not answered and the limit has not passed");
        clock.Advance(AlertDeliveryClient.Timeout);

        (await send.WaitAsync(Eventually.DefaultPatience)).Should().Equal(Unresolvable);
        handler.Requested.Should().Equal(Answers);
    }

    [Fact]
    public async Task Chat_bot_provider_fails_a_bot_that_does_not_answer_with_a_timeout()
    {
        var provider = ChatBot(new NeverAnswers(), ShortLimit);

        var send = () => provider.SendAsync(
            Guid.NewGuid(), ChannelType.DiscordDm, "u1", Payload(), CancellationToken.None);

        (await send.Should().ThrowAsync<TimeoutException>()).WithMessage("*did not answer*");
    }

    [Fact]
    public async Task Chat_bot_provider_lets_the_callers_cancellation_through()
    {
        var handler = new NeverAnswers();
        var provider = ChatBot(handler, null);
        using var cts = new CancellationTokenSource();

        var send = provider.SendAsync(Guid.NewGuid(), ChannelType.DiscordDm, "u1", Payload(), cts.Token);
        await handler.Started;
        await cts.CancelAsync();

        await send.Awaiting(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_fire_records_a_webhook_that_does_not_answer_as_failed()
    {
        var channel = new AlertRuleChannelSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), ChannelType.Webhook, WebhookTestHarness.Url, null, 0);
        var seam = await SeamAsync(webhookTimeout: ShortLimit);

        await seam.Service.DispatchAsync(seam.InstanceId, [channel], Payload(), CancellationToken.None);

        var row = await seam.RowAsync();
        row.Status.Should().Be("failed");
        row.LastError.Should().Contain("1 of 1");
    }

    [Fact]
    public async Task A_fire_records_a_chat_bot_that_does_not_answer_as_failed_with_the_reason()
    {
        var channel = new AlertRuleChannelSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), ChannelType.DiscordChannel, "123456789012345678", null, 0);
        var seam = await SeamAsync(chatBotTimeout: ShortLimit);

        await seam.Service.DispatchAsync(seam.InstanceId, [channel], Payload(), CancellationToken.None);

        var row = await seam.RowAsync();
        row.Status.Should().Be("failed");
        row.LastError.Should().Contain("did not answer");
    }

    private const string WebhookRequestSenderName = Nocturne.API.Services.Alerts.Webhooks.WebhookRequestSender.HttpClientName;

    /// <summary>Refuses <c>refuse</c>, never answers the <c>hang</c> URLs, and answers 200 to the rest.</summary>
    private sealed class Scripted(string? refuse = null, string[]? hang = null) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requested = new();
        private readonly TaskCompletionSource _allHanging = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _hanging;

        public IReadOnlyCollection<string> Requested => _requested;

        /// <summary>Completes once every <c>hang</c> URL has been requested and is waiting.</summary>
        public Task AllHanging => _allHanging.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            _requested.Enqueue(url);
            if (url == refuse)
                throw new HttpRequestException(HttpRequestError.ConnectionError);
            if (hang?.Contains(url) == true)
            {
                if (Interlocked.Increment(ref _hanging) == hang.Length)
                    _allHanging.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private sealed class NeverAnswers : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("A request that never answers does not return");
        }
    }

    private static ChatBotProvider ChatBot(HttpMessageHandler handler, TimeSpan? timeout)
    {
        var client = new HttpClient(handler);
        if (timeout is { } limit)
            client.Timeout = limit;

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(ChatBotProvider.HttpClientName)).Returns(client);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WEB_URL"] = "http://nocturne-web:5173",
                ["INSTANCE_KEY"] = "test-instance-key",
            })
            .Build();

        var accessor = new Mock<ITenantAccessor>();
        accessor.Setup(a => a.Context).Returns(new TenantContext(Tenant, "acme", "Acme", true, IsDemo: false));

        return new ChatBotProvider(
            factory.Object, configuration, accessor.Object, NullLogger<ChatBotProvider>.Instance);
    }

    private sealed class SharedInMemoryFactory(string dbName) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext()
        {
            var ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
            ctx.TenantId = Tenant;
            return ctx;
        }
    }

    private sealed record Seam(AlertDeliveryService Service, Guid InstanceId, IDbContextFactory<NocturneDbContext> Db)
    {
        public async Task<AlertDeliveryEntity> RowAsync()
        {
            await using var db = await Db.CreateDbContextAsync();
            return await db.AlertDeliveries.IgnoreQueryFilters().SingleAsync();
        }
    }

    private static async Task<Seam> SeamAsync(TimeSpan? webhookTimeout = null, TimeSpan? chatBotTimeout = null)
    {
        IDbContextFactory<NocturneDbContext> db = new SharedInMemoryFactory($"delivery_timeout_{Guid.NewGuid()}");

        var excursion = new AlertExcursionEntity
        {
            Id = Guid.NewGuid(), TenantId = Tenant, AlertRuleId = Guid.NewGuid(), StartedAt = DateTime.UtcNow,
        };
        var instance = new AlertInstanceEntity
        {
            Id = Guid.NewGuid(), TenantId = Tenant, AlertExcursionId = excursion.Id, TriggeredAt = DateTime.UtcNow,
        };
        await using (var seed = await db.CreateDbContextAsync())
        {
            seed.AlertExcursions.Add(excursion);
            seed.AlertInstances.Add(instance);
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection()
            .AddSingleton(new WebhookProvider(
                WebhookTestHarness.Sender(new NeverAnswers(), webhookTimeout),
                WebhookTestHarness.Encryption(),
                NullLogger<WebhookProvider>.Instance))
            .AddSingleton(ChatBot(new NeverAnswers(), chatBotTimeout))
            .BuildServiceProvider();

        var service = new AlertDeliveryService(
            db.AsNocturneFactory(),
            Mock.Of<ITenantMembershipRepository>(),
            Mock.Of<ITenantAccessor>(a => a.TenantId == Tenant),
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IEngineAlarmRelay>(),
            services,
            NullLogger<AlertDeliveryService>.Instance,
            TimeProvider.System);

        return new Seam(service, instance.Id, db);
    }

    private static AlertPayload Payload() => new()
    {
        AlertType = AlertConditionType.Threshold,
        RuleName = "Urgent low",
        Severity = AlertRuleSeverity.Critical,
        TenantId = Tenant,
        GlucoseValue = 52,
        Trend = null,
        TrendRate = null,
        ReadingTimestamp = DateTime.UtcNow,
        SubjectName = "Synthetic",
        ExcursionId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        ActiveExcursionCount = 1,
    };
}
