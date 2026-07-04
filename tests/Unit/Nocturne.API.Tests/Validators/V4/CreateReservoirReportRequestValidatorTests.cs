using FluentValidation.TestHelper;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Validators.V4;
using Xunit;

namespace Nocturne.API.Tests.Validators.V4;

[Trait("Category", "Unit")]
public class CreateReservoirReportRequestValidatorTests
{
    private readonly CreateReservoirReportRequestValidator _validator = new();

    [Theory]
    [InlineData(0.5)]
    [InlineData(85)]
    [InlineData(1000)]
    public void ValidUnits_Pass(double units)
    {
        var result = _validator.TestValidate(new CreateReservoirReportRequest { Units = units });
        result.ShouldNotHaveValidationErrorFor(x => x.Units);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1001)]
    public void OutOfRangeUnits_Fail(double units)
    {
        var result = _validator.TestValidate(new CreateReservoirReportRequest { Units = units });
        result.ShouldHaveValidationErrorFor(x => x.Units);
    }

    [Fact]
    public void UnknownKind_Fails()
    {
        var result = _validator.TestValidate(new CreateReservoirReportRequest { Units = 10, Kind = (ReservoirReportKind)99 });
        result.ShouldHaveValidationErrorFor(x => x.Kind);
    }

    [Fact]
    public void FutureTimestamp_Fails()
    {
        var result = _validator.TestValidate(new CreateReservoirReportRequest
        {
            Units = 10,
            Timestamp = DateTimeOffset.UtcNow.AddHours(1),
        });
        result.ShouldHaveValidationErrorFor(x => x.Timestamp);
    }

    [Fact]
    public void PastAndOmittedTimestamps_Pass()
    {
        _validator.TestValidate(new CreateReservoirReportRequest { Units = 10, Timestamp = DateTimeOffset.UtcNow.AddHours(-1) })
            .ShouldNotHaveValidationErrorFor(x => x.Timestamp);
        _validator.TestValidate(new CreateReservoirReportRequest { Units = 10 })
            .ShouldNotHaveValidationErrorFor(x => x.Timestamp);
    }

    [Fact]
    public void OverlongDevice_Fails()
    {
        var result = _validator.TestValidate(new CreateReservoirReportRequest { Units = 10, Device = new string('x', 501) });
        result.ShouldHaveValidationErrorFor(x => x.Device);
    }
}
