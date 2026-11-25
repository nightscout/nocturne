using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

[Trait("Category", "Unit")]
public class TenantSlugWebhookTests : IDisposable
{
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(ct);
    }

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();

    public void Dispose() => _db.Dispose();

    private TenantService ServiceWith(Handler handler, TimeSpan timeout)
    {
        var context = _db.CreateContext();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(OperatorWebhook.SlugValidationClient))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { Timeout = timeout });
        return new TenantService(
            context.AsFactory(),
            new TenantRepository(context.AsFactory()),
            Mock.Of<ITenantMembershipRepository>(),
            Mock.Of<ISubjectRepository>(),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new OperatorConfiguration { SlugValidationWebhookUrl = "https://operator.example/slug" }),
            factory.Object,
            Mock.Of<ITenantRoleService>(),
            grantRevocation: null!,
            new FakeTimeProvider(),
            NullLogger<TenantService>.Instance,
            AccountLinkRoutes.Off, InertTeamStaff.Announcer());
    }

    private static Handler Silent(TaskCompletionSource? arrived = null) => new(async ct =>
    {
        arrived?.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return new HttpResponseMessage(HttpStatusCode.OK);
    });

    [Fact]
    public async Task AWebhookThatDoesNotAnswerInTime_LeavesTheSlugValid()
    {
        var service = ServiceWith(Silent(), TimeSpan.FromMilliseconds(50));

        var result = await service.ValidateSlugAsync("fresh-slug");

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task AWebhookThatRejectsTheSlug_RejectsIt()
    {
        var service = ServiceWith(
            new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"isValid":false,"message":"no"}""", Encoding.UTF8, "application/json"),
            })),
            TimeSpan.FromSeconds(5));

        var result = await service.ValidateSlugAsync("fresh-slug");

        result.IsValid.Should().BeFalse();
        result.Message.Should().Be("no");
    }

    [Fact]
    public async Task ACallerThatCancels_IsNotSwallowedAsAWebhookFailure()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = ServiceWith(Silent(arrived), TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        var call = service.ValidateSlugAsync("fresh-slug", cts.Token);
        await arrived.Task;
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
    }
}
