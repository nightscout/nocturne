using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Validators.Alerts;

/// <summary>
/// Validates <see cref="UpdateAlertRuleRequest"/> for the V4 alert rule update endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Name is required and capped at 200 characters.</description></item>
/// <item><description>Description, when provided, is capped at 2,000 characters.</description></item>
/// <item><description>ConditionType must be a valid enum value.</description></item>
/// <item><description>Severity, when provided, must be a valid enum value.</description></item>
/// <item><description>AutoResolveParams is required when AutoResolveEnabled is true.</description></item>
/// <item><description>OnUnknown, when provided, is one of <see cref="AlertRuleOnUnknown"/>.</description></item>
/// <item><description>GlucoseFreshnessMinutes, when provided, is in <see cref="AlertRuleGlucoseFreshness"/>'s range.</description></item>
/// <item><description>ClearGlucoseFreshness is not sent with a GlucoseFreshnessMinutes.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="UpdateAlertRuleRequest"/>
/// <seealso cref="AlertRulesController"/>
public class UpdateAlertRuleRequestValidator : AbstractValidator<UpdateAlertRuleRequest>
{
    public UpdateAlertRuleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000).When(x => x.Description is not null);
        RuleFor(x => x.ConditionType).IsInEnum();
        RuleFor(x => x.Severity).IsInEnum().When(x => x.Severity is not null);
        RuleFor(x => x.AutoResolveParams).NotNull()
            .When(x => x.AutoResolveEnabled)
            .WithMessage("AutoResolveParams is required when AutoResolveEnabled is true");
        RuleFor(x => x.OnUnknown).Must(AlertRuleOnUnknown.IsKnown)
            .When(x => x.OnUnknown is not null)
            .WithMessage(AlertRuleOnUnknown.InvalidMessage);
        RuleFor(x => x.GlucoseFreshnessMinutes).Must(AlertRuleGlucoseFreshness.IsValid)
            .WithMessage(AlertRuleGlucoseFreshness.InvalidMessage);
        RuleFor(x => x.ClearGlucoseFreshness).Equal(false)
            .When(x => x.GlucoseFreshnessMinutes is not null)
            .WithMessage("ClearGlucoseFreshness cannot be sent with a GlucoseFreshnessMinutes");
    }
}
