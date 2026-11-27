using FluentValidation;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Validators.V4;

/// <summary>Validates <see cref="FlagRuleRequest"/>: its fact (<see cref="FlagFactFieldRules"/>), and a level a rule may give.</summary>
public class FlagRuleRequestValidator : AbstractValidator<FlagRuleRequest>
{
    public FlagRuleRequestValidator()
    {
        this.AddFactRules();
        RuleFor(x => x.Level)
            .Must(level => level is null || FlagRuleLimits.Levels.Contains(level.Value))
            .WithMessage("Send a level of info, warn or hazard.");
    }
}
