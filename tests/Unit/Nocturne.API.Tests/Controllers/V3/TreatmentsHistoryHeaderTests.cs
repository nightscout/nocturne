using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// AAPS advances its incremental-sync cursor from the <c>Last-Modified</c> and <c>ETag</c>
/// headers on <c>history/{lastModified}</c> responses. Without them the cursor never moves
/// and AAPS re-requests the same page indefinitely.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentsHistoryHeaderTests
{
    private static TreatmentsController CreateController(Mock<ITreatmentService> treatmentService)
    {
        return new TreatmentsController(
            Mock.Of<ITreatmentStore>(),
            Mock.Of<IDocumentProcessingService>(),
            treatmentService.Object,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
    }

    [Fact]
    public async Task GetTreatmentHistory_SetsCursorHeadersFromNewestRecord()
    {
        var newest = new DateTimeOffset(2024, 3, 26, 12, 5, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var older = new DateTimeOffset(2024, 3, 26, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var treatmentService = new Mock<ITreatmentService>();
        treatmentService
            .Setup(s => s.GetTreatmentsModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new Treatment { EventType = "Note", SrvModified = older },
                new Treatment { EventType = "Note", SrvModified = newest },
            });

        var controller = CreateController(treatmentService);
        await controller.GetTreatmentHistory(0);

        var expected = DateTimeOffset.FromUnixTimeMilliseconds(newest).UtcDateTime.ToString("R");
        controller.Response.Headers["Last-Modified"].ToString().Should().Be(expected);
        controller.Response.Headers["ETag"].ToString().Should().Be($"W/\"{newest}\"");
    }

    [Fact]
    public async Task GetTreatmentHistory_EmptyResult_SetsNoCursorHeaders()
    {
        var treatmentService = new Mock<ITreatmentService>();
        treatmentService
            .Setup(s => s.GetTreatmentsModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Treatment>());

        var controller = CreateController(treatmentService);
        await controller.GetTreatmentHistory(0);

        controller.Response.Headers.Should().NotContainKey("Last-Modified");
        controller.Response.Headers.Should().NotContainKey("ETag");
    }
}
