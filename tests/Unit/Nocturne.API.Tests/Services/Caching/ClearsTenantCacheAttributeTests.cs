using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Services.Caching;

[Trait("Category", "Unit")]
public class ClearsTenantCacheAttributeTests
{
    private static readonly Guid TenantId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private readonly Mock<ITenantCache> _cache = new();

    private async Task RunAsync(string method, IActionResult? result, Exception? exception = null)
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.Setup(t => t.IsResolved).Returns(true);
        tenant.Setup(t => t.TenantId).Returns(TenantId);
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(tenant.Object)
                .AddSingleton(_cache.Object)
                .BuildServiceProvider(),
        };
        http.Request.Method = method;
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var executing = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), new object());
        var executed = new ActionExecutedContext(action, [], new object()) { Result = result, Exception = exception };

        await new ClearsTenantCacheAttribute().OnActionExecutionAsync(executing, () => Task.FromResult(executed));
    }

    private void VerifyCleared(int times) =>
        _cache.Verify(c => c.RemoveTenantAsync(TenantId, It.IsAny<CancellationToken>()), Times.Exactly(times));

    [Theory]
    [InlineData(typeof(BolusController))]
    [InlineData(typeof(NutritionController))]
    [InlineData(typeof(TempBasalController))]
    public void Every_v4_writer_feeding_the_legacy_projections_carries_the_filter(Type controller) =>
        controller.GetCustomAttribute<ClearsTenantCacheAttribute>(inherit: true).Should().NotBeNull();

    [Fact]
    public async Task A_successful_write_clears_the_tenant()
    {
        await RunAsync(HttpMethods.Post, new CreatedResult("/x", new object()));

        VerifyCleared(1);
    }

    [Fact]
    public async Task A_typed_result_without_a_status_counts_as_success()
    {
        await RunAsync(HttpMethods.Put, new ObjectResult(new object()));

        VerifyCleared(1);
    }

    [Fact]
    public async Task A_read_clears_nothing()
    {
        await RunAsync(HttpMethods.Get, new OkObjectResult(new object()));

        VerifyCleared(0);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_refused_write_clears_nothing(IActionResult result)
    {
        await RunAsync(HttpMethods.Post, result);

        VerifyCleared(0);
    }

    public static TheoryData<IActionResult> Refusals() =>
    [
        new ForbidResult(),
        new StatusCodeResult(StatusCodes.Status403Forbidden),
        new BadRequestObjectResult(new ValidationProblemDetails()),
        new ConflictObjectResult(new object()),
        new ObjectResult(new ProblemDetails()),
    ];

    [Fact]
    public async Task A_write_that_threw_clears_nothing()
    {
        await RunAsync(HttpMethods.Delete, null, new InvalidOperationException());

        VerifyCleared(0);
    }
}
