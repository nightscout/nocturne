using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Services;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class DeviceAgeControllerTests
{
    private readonly Guid _subjectId = Guid.NewGuid();
    private readonly Mock<ILegacyDeviceAgeService> _deviceAgeServiceMock;
    private readonly DeviceAgeController _controller;

    public DeviceAgeControllerTests()
    {
        _deviceAgeServiceMock = new Mock<ILegacyDeviceAgeService>();
        _controller = new DeviceAgeController(
            _deviceAgeServiceMock.Object,
            Mock.Of<ILogger<DeviceAgeController>>()
        );

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = _subjectId
        };

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task GetCannulaAge_WithDefaultParameters_ReturnsOkResult()
    {
        var expectedResult = new DeviceAgeInfo { Found = true, Age = 10 };

        _deviceAgeServiceMock
            .Setup(x =>
                x.GetCannulaAgeAsync(
                    _subjectId.ToString(),
                    It.IsAny<DeviceAgePreferences>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedResult);

        var result = await _controller.GetCannulaAge();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var actualResult = Assert.IsType<DeviceAgeInfo>(okResult.Value);
        Assert.Equal(expectedResult.Found, actualResult.Found);
        Assert.Equal(expectedResult.Age, actualResult.Age);
    }

    [Fact]
    public async Task GetSensorAge_WithCustomParameters_CallsServiceWithCorrectPreferences()
    {
        _deviceAgeServiceMock
            .Setup(x =>
                x.GetSensorAgeAsync(
                    _subjectId.ToString(),
                    It.IsAny<DeviceAgePreferences>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SensorAgeInfo());

        await _controller.GetSensorAge(
            info: 100,
            warn: 150,
            urgent: 170,
            display: "days",
            enableAlerts: true
        );

        _deviceAgeServiceMock.Verify(
            x =>
                x.GetSensorAgeAsync(
                    _subjectId.ToString(),
                    It.Is<DeviceAgePreferences>(p =>
                        p.Info == 100
                        && p.Warn == 150
                        && p.Urgent == 170
                        && p.Display == "days"
                        && p.EnableAlerts
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task GetBatteryAge_WithDefaultParameters_ReturnsOkResult()
    {
        var expectedResult = new DeviceAgeInfo { Found = true, Age = 300 };

        _deviceAgeServiceMock
            .Setup(x =>
                x.GetBatteryAgeAsync(
                    _subjectId.ToString(),
                    It.IsAny<DeviceAgePreferences>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedResult);

        var result = await _controller.GetBatteryAge();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var actualResult = Assert.IsType<DeviceAgeInfo>(okResult.Value);
        Assert.Equal(expectedResult.Found, actualResult.Found);
        Assert.Equal(expectedResult.Age, actualResult.Age);
    }

    [Fact]
    public async Task GetAllDeviceAges_ReturnsAllDeviceAgeInformation()
    {
        var cannulaAge = new DeviceAgeInfo { Found = true, Age = 48 };
        var sensorAge = new SensorAgeInfo();
        var insulinAge = new DeviceAgeInfo { Found = true, Age = 24 };
        var batteryAge = new DeviceAgeInfo { Found = true, Age = 312 };

        _deviceAgeServiceMock
            .Setup(x =>
                x.GetCannulaAgeAsync(
                    _subjectId.ToString(),
                    It.IsAny<DeviceAgePreferences>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(cannulaAge);

        _deviceAgeServiceMock
            .Setup(x =>
                x.GetSensorAgeAsync(
                    _subjectId.ToString(),
                    It.IsAny<DeviceAgePreferences>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(sensorAge);

        _deviceAgeServiceMock
            .Setup(x =>
                x.GetInsulinAgeAsync(
                    _subjectId.ToString(),
                    It.IsAny<DeviceAgePreferences>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(insulinAge);

        _deviceAgeServiceMock
            .Setup(x =>
                x.GetBatteryAgeAsync(
                    _subjectId.ToString(),
                    It.IsAny<DeviceAgePreferences>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(batteryAge);

        var result = await _controller.GetAllDeviceAges();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value;
        Assert.NotNull(value);

        var valueType = value!.GetType();
        Assert.Same(cannulaAge, valueType.GetProperty("cage")?.GetValue(value));
        Assert.Same(sensorAge, valueType.GetProperty("sage")?.GetValue(value));
        Assert.Same(insulinAge, valueType.GetProperty("iage")?.GetValue(value));
        Assert.Same(batteryAge, valueType.GetProperty("bage")?.GetValue(value));
    }
}
