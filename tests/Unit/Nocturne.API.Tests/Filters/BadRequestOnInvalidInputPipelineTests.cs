using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Filters;

/// <summary>
/// Runs the filter through the real pipeline rather than against a hand-built
/// <c>ExceptionContext</c>: a bare <c>Attribute : IExceptionFilter</c> on the controller class has
/// to be discovered as filter metadata, run for an async action, and answer before
/// <c>UseExceptionHandler</c> turns the exception into a 500.
/// </summary>
[Trait("Category", "Unit")]
public class BadRequestOnInvalidInputPipelineTests : IClassFixture<GoldenFileWebAppFactory>
{
    private const string Message = "Not enough data points to calculate daily ratios";

    private readonly HttpClient _client;

    public BadRequestOnInvalidInputPipelineTests(GoldenFileWebAppFactory factory)
    {
        var statistics = new Mock<IStatisticsService>();
        statistics
            .Setup(s =>
                s.CalculateDailyBasalBolusRatios(
                    It.IsAny<IEnumerable<Bolus>>(),
                    It.IsAny<IEnumerable<Bolus>>(),
                    It.IsAny<IEnumerable<TempBasal>>(),
                    It.IsAny<TimeZoneInfo?>(),
                    It.IsAny<IEnumerable<BasalInjection>?>()
                )
            )
            .Throws(new ArgumentException(Message));

        _client = factory
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services => services.AddScoped(_ => statistics.Object))
            )
            .CreateClient();
    }

    [Fact]
    public async Task AnArgumentExceptionFromAnAsyncAction_Is400ProblemDetailsWithTheMessageInDetail()
    {
        var response = await _client.GetAsync(
            "/api/v4/statistics/daily-basal-bolus-ratios"
                + "?startDate=2026-01-01T00:00:00Z&endDate=2026-01-08T00:00:00Z"
        );

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the body was: {0}", body);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(400);
        problem.Title.Should().Be("Bad Request");
        problem.Detail.Should().Be(Message);
    }
}
