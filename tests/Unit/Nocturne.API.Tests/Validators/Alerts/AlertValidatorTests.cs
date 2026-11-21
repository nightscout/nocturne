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
