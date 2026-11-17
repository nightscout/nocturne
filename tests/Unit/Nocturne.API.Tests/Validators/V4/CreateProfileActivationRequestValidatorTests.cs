using FluentValidation.TestHelper;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Validators.V4;
using Xunit;

namespace Nocturne.API.Tests.Validators.V4;

public class CreateProfileActivationRequestValidatorTests
{
    private readonly CreateProfileActivationRequestValidator _validator = new();

    private static CreateProfileActivationRequest Valid() => new() { BaseProfileName = "Rhys" };

    [Fact]
    public void A_plain_switch_passes() => _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData(30, 23, 10080)]
    [InlineData(250, -23, 0)]
    public void The_bounds_AAPS_allows_pass(int percentage, int timeshift, int duration)
    {
        var request = Valid();
        request.Percentage = percentage;
        request.TimeshiftHours = timeshift;
        request.DurationMinutes = duration;
        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void A_missing_profile_name_fails()
    {
        var request = Valid();
        request.BaseProfileName = "";
        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.BaseProfileName);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(251)]
    [InlineData(0)]
    public void A_percentage_outside_30_to_250_fails(int percentage)
    {
        var request = Valid();
        request.Percentage = percentage;
        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Percentage);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(-24)]
    public void A_timeshift_beyond_23_hours_fails(int hours)
    {
        var request = Valid();
        request.TimeshiftHours = hours;
        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.TimeshiftHours);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10081)]
    public void A_duration_outside_a_week_fails(int minutes)
    {
        var request = Valid();
        request.DurationMinutes = minutes;
        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.DurationMinutes);
    }
}
