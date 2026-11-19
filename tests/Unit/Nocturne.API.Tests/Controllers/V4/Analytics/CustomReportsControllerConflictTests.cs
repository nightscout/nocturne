using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Reports;
using Nocturne.API.Tests.TestDoubles;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

[Trait("Category", "Unit")]
public sealed class CustomReportsControllerConflictTests
{
    private readonly Mock<IReportSpecStore> _store = new();

    private CustomReportsController Controller() =>
        new(_store.Object, Mock.Of<ICustomReportRunner>(), Mock.Of<ICustomReportCatalogue>(), Mock.Of<ICustomReportSignalAvailability>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            ProblemDetailsFactory = new EchoingProblemDetailsFactory(),
        };

    [Fact]
    public async Task A_stale_update_is_a_409_carrying_the_changed_code_and_the_expected_value_reaches_the_store()
    {
        var read = new DateTime(2026, 11, 19, 1, 2, 3, DateTimeKind.Utc).AddTicks(4560);
        _store.Setup(s => s.UpdateAsync(It.IsAny<Guid>(), "Renamed", null, read, It.IsAny<CustomReportEditor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CustomReportWrite(CustomReportWriteStatus.Changed, null, []));

        var result = await Controller().UpdateCustomReport(
            Guid.CreateVersion7(), new CustomReportUpdateRequest { Name = "Renamed", ExpectedUpdatedAt = read }, CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.Value.Should().BeAssignableTo<ProblemDetails>().Which.Extensions
            .Should().Contain("code", CustomReportProblemCodes.Changed);
        CustomReportProblemCodes.Changed.Should().Be("custom_report_changed");
    }

    [Fact]
    public async Task An_update_without_the_expected_value_passes_null_to_the_store()
    {
        _store.Setup(s => s.UpdateAsync(It.IsAny<Guid>(), "Renamed", null, null, It.IsAny<CustomReportEditor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CustomReportWrite(CustomReportWriteStatus.NotFound, null, []));

        var result = await Controller().UpdateCustomReport(
            Guid.CreateVersion7(), new CustomReportUpdateRequest { Name = "Renamed" }, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        _store.VerifyAll();
    }
}
