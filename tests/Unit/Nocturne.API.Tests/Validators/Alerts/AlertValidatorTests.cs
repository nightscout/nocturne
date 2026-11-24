using FluentValidation.TestHelper;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Validators.Alerts;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Validators.Alerts;

public class SnoozeRequestValidatorTests
{
    private readonly SnoozeRequestValidator _validator = new();

    [Fact]
    public void Valid_snooze_passes()
    {
        var result = _validator.TestValidate(new SnoozeRequest { Minutes = 30 });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Zero_minutes_fails()
    {
        var result = _validator.TestValidate(new SnoozeRequest { Minutes = 0 });
        result.ShouldHaveValidationErrorFor(x => x.Minutes);
    }

    [Fact]
    public void Negative_minutes_fails()
    {
        var result = _validator.TestValidate(new SnoozeRequest { Minutes = -5 });
        result.ShouldHaveValidationErrorFor(x => x.Minutes);
    }

    [Fact]
    public void Minutes_exceeding_1440_fails()
    {
        var result = _validator.TestValidate(new SnoozeRequest { Minutes = 1441 });
        result.ShouldHaveValidationErrorFor(x => x.Minutes);
    }

    [Fact]
    public void Exactly_1440_minutes_passes()
    {
        var result = _validator.TestValidate(new SnoozeRequest { Minutes = 1440 });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Exactly_1_minute_passes()
    {
        var result = _validator.TestValidate(new SnoozeRequest { Minutes = 1 });
        result.ShouldNotHaveAnyValidationErrors();
    }
}

public class AlertRuleGlucoseFreshnessValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(1440)]
    public void Accepts_absent_or_a_bound_up_to_a_day(int? minutes)
    {
        new CreateAlertRuleRequestValidator()
            .TestValidate(new CreateAlertRuleRequest { Name = "Low", GlucoseFreshnessMinutes = minutes })
            .ShouldNotHaveValidationErrorFor(x => x.GlucoseFreshnessMinutes);
        new UpdateAlertRuleRequestValidator()
            .TestValidate(new UpdateAlertRuleRequest { Name = "Low", GlucoseFreshnessMinutes = minutes })
            .ShouldNotHaveValidationErrorFor(x => x.GlucoseFreshnessMinutes);
    }

    [Fact]
    public void An_update_may_clear_the_bound_or_set_it_but_not_both()
    {
        var validator = new UpdateAlertRuleRequestValidator();

        validator.TestValidate(new UpdateAlertRuleRequest { Name = "Low", ClearGlucoseFreshness = true })
            .ShouldNotHaveValidationErrorFor(x => x.ClearGlucoseFreshness);
        validator.TestValidate(new UpdateAlertRuleRequest
            {
                Name = "Low", GlucoseFreshnessMinutes = 30, ClearGlucoseFreshness = true,
            })
            .ShouldHaveValidationErrorFor(x => x.ClearGlucoseFreshness);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1441)]
    [InlineData(int.MaxValue)]
    public void Rejects_a_bound_outside_one_minute_to_a_day(int minutes)
    {
        new CreateAlertRuleRequestValidator()
            .TestValidate(new CreateAlertRuleRequest { Name = "Low", GlucoseFreshnessMinutes = minutes })
            .ShouldHaveValidationErrorFor(x => x.GlucoseFreshnessMinutes);
        new UpdateAlertRuleRequestValidator()
            .TestValidate(new UpdateAlertRuleRequest { Name = "Low", GlucoseFreshnessMinutes = minutes })
            .ShouldHaveValidationErrorFor(x => x.GlucoseFreshnessMinutes);
    }
}

public class AlertRuleOnUnknownValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(AlertRuleOnUnknown.Hold)]
    [InlineData(AlertRuleOnUnknown.Met)]
    [InlineData(AlertRuleOnUnknown.NotMet)]
    public void Accepts_absent_or_one_of_the_three_words(string? word)
    {
        new CreateAlertRuleRequestValidator()
            .TestValidate(new CreateAlertRuleRequest { Name = "Low", OnUnknown = word })
            .ShouldNotHaveValidationErrorFor(x => x.OnUnknown);
        new UpdateAlertRuleRequestValidator()
            .TestValidate(new UpdateAlertRuleRequest { Name = "Low", OnUnknown = word })
            .ShouldNotHaveValidationErrorFor(x => x.OnUnknown);
    }

    [Theory]
    [InlineData("Hold")]
    [InlineData("ignore")]
    [InlineData("")]
    public void Rejects_any_other_word(string word)
    {
        new CreateAlertRuleRequestValidator()
            .TestValidate(new CreateAlertRuleRequest { Name = "Low", OnUnknown = word })
            .ShouldHaveValidationErrorFor(x => x.OnUnknown);
        new UpdateAlertRuleRequestValidator()
            .TestValidate(new UpdateAlertRuleRequest { Name = "Low", OnUnknown = word })
            .ShouldHaveValidationErrorFor(x => x.OnUnknown);
    }
}
