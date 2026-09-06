using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Connectors;
using Nocturne.API.Services.Alerts.Webhooks;
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

    private static ObjectResult ProblemOf(ActionResult<WebhookNotificationSettings> result) =>
        result.Result.Should().BeOfType<ObjectResult>().Subject;

    private static WebhookSettingsController NewController()
    {
        var services = new ServiceCollection();
        services.AddControllers();

        return new WebhookSettingsController(
            new WebhookRequestSender(
                Mock.Of<IHttpClientFactory>(),
                NullLogger<WebhookRequestSender>.Instance),
            NullLogger<WebhookSettingsController>.Instance)
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
