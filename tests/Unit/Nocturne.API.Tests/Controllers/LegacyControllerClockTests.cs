using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V1;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// The legacy controllers stamp documents and answer <c>serverTime</c> from the injected clock, so
/// each stamp can be asserted against one fixed instant.
/// </summary>
[Trait("Category", "Unit")]
public class LegacyControllerClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 15, 9, 26, 535, TimeSpan.Zero);

    private static ControllerContext Context(string? accept = null)
    {
        var http = new DefaultHttpContext();
        if (accept is not null)
            http.Request.Headers.Accept = accept;
        return new ControllerContext { HttpContext = http };
    }

    [Fact]
    public async Task A_v1_food_posted_without_created_at_is_stamped_with_the_clock()
    {
        IReadOnlyCollection<Food>? stored = null;
        var foods = new Mock<IFoodRepository>();
        foods
            .Setup(r => r.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Food>, CancellationToken>((f, _) => stored = f.ToList())
            .ReturnsAsync((IEnumerable<Food> f, CancellationToken _) => f);
        var controller = new Nocturne.API.Controllers.V1.FoodController(
            foods.Object,
            NullLogger<Nocturne.API.Controllers.V1.FoodController>.Instance,
            new FakeTimeProvider(Now)) { ControllerContext = Context() };

        await controller.CreateFood(JsonDocument.Parse("""{"name":"Apple"}""").RootElement);

        stored.Should().ContainSingle().Which.CreatedAt.Should().Be("2026-03-14T15:09:26.535Z");
    }

    [Fact]
    public async Task A_v1_food_read_sends_the_clock_as_last_modified()
    {
        var foods = new Mock<IFoodRepository>();
        foods
            .Setup(r => r.GetFoodAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Food>());
        var controller = new Nocturne.API.Controllers.V1.FoodController(
            foods.Object,
            NullLogger<Nocturne.API.Controllers.V1.FoodController>.Instance,
            new FakeTimeProvider(Now)) { ControllerContext = Context() };

        await controller.GetFood();

        controller.Response.Headers["Last-Modified"].ToString().Should().Be("Sat, 14 Mar 2026 15:09:26 GMT");
    }

    [Fact]
    public async Task The_v1_status_error_body_carries_the_clock_as_server_time()
    {
        var status = new Mock<IStatusService>();
        status.Setup(s => s.GetSystemStatusAsync()).ThrowsAsync(new InvalidOperationException("down"));
        var controller = new StatusController(
            status.Object, NullLogger<StatusController>.Instance, new FakeTimeProvider(Now))
        {
            ControllerContext = Context("application/json"),
        };

        var result = await controller.GetStatus();

        var body = result.Should().BeOfType<JsonResult>().Subject.Value.Should().BeOfType<StatusResponse>().Subject;
        body.ServerTime.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task The_v3_status_error_body_carries_the_clock_as_server_time()
    {
        var status = new Mock<IStatusService>();
        status.Setup(s => s.GetV3SystemStatusAsync()).ThrowsAsync(new InvalidOperationException("down"));
        var controller = new Nocturne.API.Controllers.V3.StatusController(
            status.Object,
            NullLogger<Nocturne.API.Controllers.V3.StatusController>.Instance,
            new FakeTimeProvider(Now)) { ControllerContext = Context() };

        var result = await controller.GetStatus();

        var envelope = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<V3Envelope<V3StatusResponse>>().Subject;
        envelope.Result!.ServerTime.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task A_v3_settings_put_stamps_srv_modified_with_the_clock()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext($"v3_clock_{Guid.NewGuid()}");
        context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var controller = new Nocturne.API.Controllers.V3.SettingsController(
            new SettingsRepository(context.AsFactory()),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<Nocturne.API.Controllers.V3.SettingsController>.Instance,
            new FakeTimeProvider(Now)) { ControllerContext = Context() };

        var result = await controller.UpdateSettings(
            "aaps", JsonDocument.Parse("""{"app":"AAPS","date":1792481008798}""").RootElement);

        var created = result.Should().BeAssignableTo<ObjectResult>().Subject;
        JsonSerializer.SerializeToElement(created.Value, NightscoutJsonOptions.Create())
            .GetProperty("lastModified").GetInt64().Should().Be(Now.ToUnixTimeMilliseconds());
        context.Settings.Single().SrvModified.Should().Be(Now);
    }
}
