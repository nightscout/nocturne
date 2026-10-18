using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class LabHbA1cControllerTests
{
    [Theory]
    [InlineData(false, "percent", 7.0)]
    [InlineData(true, "percent", 7.0)]
    [InlineData(false, "mmol/mol", 53.00565)]
    public async Task Create_normalizes_new_units_and_legacy_percent_without_changing_storage(bool legacy, string unit, double value)
    {
        var repository = new Mock<ILabHbA1cResultRepository>();
        LabHbA1cResult? saved = null;
        repository.Setup(r => r.CreateAsync(It.IsAny<LabHbA1cResult>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LabHbA1cResult result, CancellationToken _) => { saved = result; return result; });
        var controller = new LabHbA1cController(repository.Object);
        var response = await controller.Create(new CreateLabHbA1cResultRequest
        {
            Value = legacy ? null : value,
            ValuePercent = legacy ? value : null,
            Unit = unit,
            MeasuredAt = new DateTime(2025, 1, 1),
        });
        response.Result.Should().BeOfType<OkObjectResult>();
        saved!.ValuePercent.Should().BeApproximately(7, 0.000001);
        saved.A1cDisplay.MmolMol.Should().BeApproximately(53.00565, 0.000001);
    }

    [Theory]
    [InlineData(null, null, "percent")]
    [InlineData(7.0, 7.0, "percent")]
    [InlineData(null, 7.0, "mmol/mol")]
    [InlineData(7.0, null, "mmol/L")]
    [InlineData(-1.0, null, "mmol/mol")]
    [InlineData(300.0, null, "mmol/mol")]
    [InlineData(double.NaN, null, "percent")]
    [InlineData(double.PositiveInfinity, null, "mmol/mol")]
    public async Task Create_rejects_ambiguous_invalid_and_out_of_range_values(double? value, double? legacy, string unit)
    {
        var repository = new Mock<ILabHbA1cResultRepository>();
        var response = await new LabHbA1cController(repository.Object).Create(new CreateLabHbA1cResultRequest
        {
            Value = value, ValuePercent = legacy, Unit = unit, MeasuredAt = new DateTime(2025, 1, 1),
        });
        response.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        repository.Verify(r => r.CreateAsync(It.IsAny<LabHbA1cResult>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
