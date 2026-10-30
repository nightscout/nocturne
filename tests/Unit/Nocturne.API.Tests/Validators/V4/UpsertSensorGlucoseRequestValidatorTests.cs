using FluentValidation.TestHelper;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Validators.V4;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Validators.V4;

public class UpsertSensorGlucoseRequestValidatorTests
{
    private readonly UpsertSensorGlucoseRequestValidator _validator = new();

    private static UpsertSensorGlucoseRequest ValidRequest() => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Mgdl = 120,
        Direction = GlucoseDirection.Flat,
    };

    [Fact]
    public void Valid_request_passes()
    {
        var result = _validator.TestValidate(ValidRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Default_timestamp_fails()
    {
        var request = ValidRequest();
        request.Timestamp = default;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Timestamp);
    }

    [Fact]
    public void Mgdl_negative_fails()
    {
        var request = ValidRequest();
        request.Mgdl = -1;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Mgdl);
    }

    [Fact]
    public void Mgdl_above_10000_fails()
    {
        var request = ValidRequest();
        request.Mgdl = 10001;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Mgdl);
    }

    [Fact]
    public void Invalid_direction_fails()
    {
        var request = ValidRequest();
        request.Direction = (GlucoseDirection)999;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Direction);
    }

    [Fact]
    public void Null_direction_passes()
    {
        var request = ValidRequest();
        request.Direction = null;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Direction);
    }

    [Fact]
    public void Quality_and_sensor_status_pass()
    {
        var request = ValidRequest();
        request.Quality = SensorGlucoseQuality.Invalid;
        request.SensorStatus = "G7:Errors/info2";
        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Undefined_quality_fails()
    {
        var request = ValidRequest();
        request.Quality = (SensorGlucoseQuality)999;
        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Quality);
    }

    [Fact]
    public void Sensor_status_longer_than_its_column_fails()
    {
        var request = ValidRequest();
        request.SensorStatus = new string('x', 65);
        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.SensorStatus);
    }
}
