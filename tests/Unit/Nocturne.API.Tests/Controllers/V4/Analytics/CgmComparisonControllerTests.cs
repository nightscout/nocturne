using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Analytics;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

/// <summary>
/// Tests the controller's guard rails — window, tolerance and device validation — and that a
/// valid request echoes the devices and window it compared.
/// </summary>
public class CgmComparisonControllerTests
{
    private readonly Mock<ISensorGlucoseRepository> _glucose = new();
    private readonly Mock<IPatientDeviceRepository> _devices = new();
    private readonly CgmComparisonController _controller;

    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid DeviceA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DeviceB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public CgmComparisonControllerTests()
    {
        _controller = new CgmComparisonController(_glucose.Object, _devices.Object);
    }

    private void RegisterDevice(Guid id, string model, DeviceCategory category = DeviceCategory.CGM) =>
        _devices
            .Setup(d => d.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PatientDevice { Id = id, Model = model, DeviceCategory = category });

    private void RegisterReadings(Guid patientDeviceId, params (double Minutes, double Mgdl)[] readings) =>
        _glucose
            .Setup(g => g.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(),
                It.Is<Guid?>(id => id == patientDeviceId)))
            .ReturnsAsync(readings.Select(r => new SensorGlucose
            {
                Timestamp = Start.AddMinutes(r.Minutes),
                Mgdl = r.Mgdl,
            }));

    private void VerifyNoRead() =>
        _glucose.Verify(g => g.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>()),
            Times.Never);

    [Fact]
    public async Task Compare_with_missing_dates_returns_bad_request_without_reading()
    {
        var result = await _controller.Compare(DeviceA, DeviceB, default, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        VerifyNoRead();
    }

    [Fact]
    public async Task Compare_with_end_not_after_start_returns_bad_request()
    {
        var result = await _controller.Compare(DeviceA, DeviceB, End, Start);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        VerifyNoRead();
    }

    [Fact]
    public async Task Compare_with_a_range_over_ninety_days_returns_bad_request()
    {
        var result = await _controller.Compare(DeviceA, DeviceB, Start, Start.AddDays(90).AddSeconds(1));

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        VerifyNoRead();
    }

    [Fact]
    public async Task Compare_accepts_a_range_of_exactly_ninety_days()
    {
        RegisterDevice(DeviceA, "Sensor A");
        RegisterDevice(DeviceB, "Sensor B");
        RegisterReadings(DeviceA, (0, 100));
        RegisterReadings(DeviceB, (1, 104));

        var result = await _controller.Compare(DeviceA, DeviceB, Start, Start.AddDays(90));

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Compare_with_the_same_device_on_both_sides_returns_bad_request()
    {
        var result = await _controller.Compare(DeviceA, DeviceA, Start, End);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        VerifyNoRead();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(30.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Compare_with_a_tolerance_outside_the_allowed_band_returns_bad_request(double tolerance)
    {
        var result = await _controller.Compare(DeviceA, DeviceB, Start, End, tolerance);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        VerifyNoRead();
    }

    [Fact]
    public async Task Compare_with_an_unregistered_device_returns_not_found()
    {
        RegisterDevice(DeviceA, "Sensor A");
        _devices
            .Setup(d => d.GetByIdAsync(DeviceB, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientDevice?)null);

        var result = await _controller.Compare(DeviceA, DeviceB, Start, End);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
        VerifyNoRead();
    }

    [Fact]
    public async Task Compare_with_a_non_cgm_device_returns_bad_request()
    {
        RegisterDevice(DeviceA, "Sensor A");
        RegisterDevice(DeviceB, "Omnipod 5", DeviceCategory.InsulinPump);

        var result = await _controller.Compare(DeviceA, DeviceB, Start, End);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        VerifyNoRead();
    }

    [Fact]
    public async Task Compare_echoes_the_devices_and_window_it_compared()
    {
        RegisterDevice(DeviceA, "Sensor A");
        RegisterDevice(DeviceB, "Sensor B");
        RegisterReadings(DeviceA, (0, 110), (5, 120), (60, 130));
        RegisterReadings(DeviceB, (1, 100), (6, 100));

        var result = await _controller.Compare(DeviceA, DeviceB, Start, End);

        var payload = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<CgmComparisonResult>().Subject;

        payload.DeviceAId.Should().Be(DeviceA);
        payload.DeviceAName.Should().Be("Sensor A");
        payload.DeviceBId.Should().Be(DeviceB);
        payload.DeviceBName.Should().Be("Sensor B");
        payload.StartDate.Should().Be(Start);
        payload.EndDate.Should().Be(End);
        payload.ToleranceMinutes.Should().Be(5);
        payload.ReadingCountA.Should().Be(3);
        payload.ReadingCountB.Should().Be(2);
        payload.Pairs.Should().HaveCount(2);
        payload.UnpairedCountA.Should().Be(1);
        payload.UnpairedCountB.Should().Be(0);
        payload.Metrics!.PairCount.Should().Be(2);
        payload.Metrics.MeanAbsoluteDifferenceMgdl.Should().Be(15);
    }

    [Fact]
    public async Task Compare_omits_the_metrics_when_nothing_paired()
    {
        RegisterDevice(DeviceA, "Sensor A");
        RegisterDevice(DeviceB, "Sensor B");
        RegisterReadings(DeviceA, (0, 110));
        RegisterReadings(DeviceB, (60, 100));

        var result = await _controller.Compare(DeviceA, DeviceB, Start, End);

        var payload = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<CgmComparisonResult>().Subject;

        payload.Pairs.Should().BeEmpty();
        payload.Metrics.Should().BeNull();
    }

    [Fact]
    public async Task Compare_normalizes_unspecified_kind_dates_to_utc()
    {
        RegisterDevice(DeviceA, "Sensor A");
        RegisterDevice(DeviceB, "Sensor B");
        RegisterReadings(DeviceA, (0, 100));
        RegisterReadings(DeviceB, (1, 104));

        var result = await _controller.Compare(
            DeviceA, DeviceB,
            DateTime.SpecifyKind(Start, DateTimeKind.Unspecified),
            DateTime.SpecifyKind(End, DateTimeKind.Unspecified));

        var payload = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<CgmComparisonResult>().Subject;

        payload.StartDate.Kind.Should().Be(DateTimeKind.Utc);
        payload.EndDate.Kind.Should().Be(DateTimeKind.Utc);
    }
}
