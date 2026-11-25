using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Connectors;
using Nocturne.API.Services.Alerts.Webhooks;
using Nocturne.API.Tests.Services.Alerts.Webhooks;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Connectors;

/// <summary>
/// Unit coverage for <see cref="WebhookSettingsController"/>'s settings pair. The tenant-wide
/// webhook record the GET/PUT shape describes has no storage in the current alert engine, so both
/// must fail visibly rather than echo a save back.
/// </summary>
[Trait("Category", "Unit")]
public class WebhookSettingsControllerTests
{
    [Fact]
    public void GetWebhookSettings_reports501_ratherThanAStubbedDisabledConfig()
    {
        var result = NewController().GetWebhookSettings();

        var problem = ProblemOf(result);
        problem.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        problem.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Contain("alert rules");
    }

    [Fact]
    public void SaveWebhookSettings_reports501_ratherThanEchoingTheBody()
    {
        var result = NewController().SaveWebhookSettings(new WebhookNotificationSettings
        {
            Enabled = true,
            Urls = ["https://example.invalid/hook"],
            Secret = "s3cret",
        });

        ProblemOf(result).StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
    }

    [Fact]
    public async Task A_test_naming_more_urls_than_the_cap_is_refused_and_sends_nothing()
    {
        var handler = new CountingHandler();
        using var host = TestHost(handler);

        var response = await host.GetTestClient().PostAsJsonAsync(
            TestRoute, new { urls = Urls(WebhookRequestSender.MaxUrls + 1), secret = "s3cret" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        handler.Sent.Should().Be(0);
    }

    [Fact]
    public async Task A_test_naming_as_many_urls_as_the_cap_sends_to_each()
    {
        var handler = new CountingHandler();
        using var host = TestHost(handler);

        var response = await host.GetTestClient().PostAsJsonAsync(
            TestRoute, new { urls = Urls(WebhookRequestSender.MaxUrls), secret = "s3cret" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Sent.Should().Be(WebhookRequestSender.MaxUrls);
    }

    private const string TestRoute = "/api/v4/ui-settings/notifications/webhooks/test";

    /// <summary>Documentation-range IP literals, which skip DNS and count as publicly routable.</summary>
    private static string[] Urls(int count) =>
        [.. Enumerable.Range(1, count).Select(i => $"https://203.0.113.{i}/hook")];

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _sent;

        public int Sent => Volatile.Read(ref _sent);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sent);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    /// <summary>
    /// The controller behind MVC model binding and validation, which a direct call on it skips,
    /// called by an authenticated caller holding every scope.
    /// </summary>
    private static IHost TestHost(HttpMessageHandler handler) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton(WebhookTestHarness.Sender(handler));
                    services.Configure<RouteOptions>(o => o.SuppressCheckForUnhandledSecurityMetadata = true);
                    services.AddControllers().ConfigureApplicationPartManager(manager =>
                    {
                        manager.ApplicationParts.Clear();
                        manager.ApplicationParts.Add(new AssemblyPart(typeof(WebhookSettingsController).Assembly));
                        manager.FeatureProviders.Add(new OnlyWebhookSettingsController());
                    });
                });
                web.Configure(app =>
                {
                    app.Use((context, next) =>
                    {
                        context.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>([Scope.FullAccess]);
                        context.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };
                        return next(context);
                    });
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();

    private sealed class OnlyWebhookSettingsController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(WebhookSettingsController).GetTypeInfo());
        }
    }

    private static ObjectResult ProblemOf(ActionResult<WebhookNotificationSettings> result) =>
        result.Result.Should().BeOfType<ObjectResult>().Subject;

    private static WebhookSettingsController NewController()
    {
        var services = new ServiceCollection();
        services.AddControllers();

        return new WebhookSettingsController(
            new WebhookRequestSender(
                Mock.Of<IHttpClientFactory>(),
                NullLogger<WebhookRequestSender>.Instance,
                TimeProvider.System))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services.BuildServiceProvider(),
                },
            },
        };
    }
}
