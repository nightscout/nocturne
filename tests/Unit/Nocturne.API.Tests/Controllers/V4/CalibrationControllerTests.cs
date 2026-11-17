using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

public class CalibrationControllerTests
{
    private readonly Mock<ICalibrationRepository> _repoMock = new();

    private CalibrationController CreateController()
    {
        var controller = new CalibrationController(_repoMock.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    [Fact]
    public async Task GetAll_FullPageWithoutIncludeTotal_LeavesTotalNullWithoutCounting()
    {
        _repoMock.As<IV4Repository<Calibration>>()
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Calibration()]);

        var result = await CreateController().GetAll(from: null, to: null, limit: 1);

        var response = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PaginatedResponse<Calibration>>().Subject;
        response.Pagination.Total.Should().BeNull();
        _repoMock.Verify(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
