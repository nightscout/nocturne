using FluentValidation.TestHelper;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Validators.V4;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Validators.V4;

[Trait("Category", "Unit")]
public class FlagRuleRequestValidatorTests
{
    private readonly FlagRuleRequestValidator _validator = new();

    private static FlagRuleRequest Valid(NotificationUrgency? level = NotificationUrgency.Warn) =>
        new("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4, level);

    [Theory]
    [InlineData(NotificationUrgency.Info)]
    [InlineData(NotificationUrgency.Warn)]
    [InlineData(NotificationUrgency.Hazard)]
    [InlineData(null)]
    public void A_level_a_rule_may_give_or_none_passes(NotificationUrgency? level) =>
        _validator.TestValidate(Valid(level)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Urgent_is_kept_for_the_fixed_set_and_refused() =>
        _validator.TestValidate(Valid(NotificationUrgency.Urgent)).ShouldHaveValidationErrorFor(x => x.Level);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_value_that_is_not_a_number_is_refused(double value) =>
        _validator.TestValidate(Valid() with { Value = value }).ShouldHaveValidationErrorFor(x => x.Value);

    [Fact]
    public void An_unknown_comparison_is_refused() =>
        _validator.TestValidate(Valid() with { Comparison = (FlagRuleComparison)9 }).ShouldHaveValidationErrorFor(x => x.Comparison);

    [Fact]
    public void A_body_without_a_comparison_or_a_value_is_refused()
    {
        _validator.TestValidate(Valid() with { Comparison = null }).ShouldHaveValidationErrorFor(x => x.Comparison);
        _validator.TestValidate(Valid() with { Value = null }).ShouldHaveValidationErrorFor(x => x.Value);
    }

    [Fact]
    public void An_empty_or_overlong_dataset_or_field_is_refused()
    {
        _validator.TestValidate(Valid() with { Dataset = "" }).ShouldHaveValidationErrorFor(x => x.Dataset);
        _validator.TestValidate(Valid() with { Field = new string('f', 65) }).ShouldHaveValidationErrorFor(x => x.Field);
    }
}
