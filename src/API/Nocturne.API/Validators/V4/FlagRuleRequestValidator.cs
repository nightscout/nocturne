using FluentValidation;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="FlagRuleRequest"/>: the dataset and field at the length of their columns, a comparison, a finite
/// value, and a level a rule may give. Whether the fact exists, and takes the range sent, is checked against the reports engine by
/// the controller.
/// </summary>
public class FlagRuleRequestValidator : AbstractValidator<FlagRuleRequest>
{
    public FlagRuleRequestValidator()
    {
        RuleFor(x => x.Dataset).NotEmpty().MaximumLength(ColumnLengths.FlagRule.Dataset);
        RuleFor(x => x.Field).NotEmpty().MaximumLength(ColumnLengths.FlagRule.Field);
        RuleFor(x => x.Comparison).NotNull().WithMessage("Send a comparison: below or above.").IsInEnum();
        RuleFor(x => x.Value).NotNull().WithMessage("Send a number.")
            .Must(value => value is null || double.IsFinite(value.Value)).WithMessage("Send a number.");
        RuleFor(x => x.Level)
            .Must(level => level is null || FlagRuleLimits.Levels.Contains(level.Value))
            .WithMessage("Send a level of info, warn or hazard.");
    }
}
