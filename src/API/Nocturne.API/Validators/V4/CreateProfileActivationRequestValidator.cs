using FluentValidation;
using Nocturne.API.Models.Requests.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="CreateProfileActivationRequest"/> against the ranges AAPS's profile switch
/// dialog allows (<c>Constants.CPP_MIN_PERCENTAGE</c>..<c>CPP_MAX_PERCENTAGE</c>,
/// <c>CPP_MIN_TIMESHIFT</c>..<c>CPP_MAX_TIMESHIFT</c>, <c>MAX_PROFILE_SWITCH_DURATION</c>), so a switch
/// recorded here could also be one AAPS runs.
/// </summary>
public class CreateProfileActivationRequestValidator : AbstractValidator<CreateProfileActivationRequest>
{
    public const int MinPercentage = 30;
    public const int MaxPercentage = 250;
    public const int MaxTimeshiftHours = 23;
    public const int MaxDurationMinutes = 7 * 24 * 60;

    public CreateProfileActivationRequestValidator()
    {
        RuleFor(x => x.BaseProfileName).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Percentage).InclusiveBetween(MinPercentage, MaxPercentage);
        RuleFor(x => x.TimeshiftHours).InclusiveBetween(-MaxTimeshiftHours, MaxTimeshiftHours);
        RuleFor(x => x.DurationMinutes).InclusiveBetween(0, MaxDurationMinutes).When(x => x.DurationMinutes is not null);
        RuleFor(x => x.Notes).MaximumLength(2000).When(x => x.Notes is not null);
    }
}
