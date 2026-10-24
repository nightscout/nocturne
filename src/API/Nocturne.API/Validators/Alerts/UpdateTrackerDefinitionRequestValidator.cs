using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Models;

namespace Nocturne.API.Validators.Alerts;

/// <summary>
/// Validates <see cref="UpdateTrackerDefinitionRequest"/> for the V4 tracker update endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Visibility, when supplied, must be a valid <see cref="TrackerVisibility"/> value.</description></item>
/// <item><description>Visibility must not be <see cref="TrackerVisibility.RoleRestricted"/>, a value
/// that was never implemented and whose stored rows were migrated to Private. Visibility itself
/// restricts no read.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="UpdateTrackerDefinitionRequest"/>
/// <seealso cref="TrackersController"/>
public class UpdateTrackerDefinitionRequestValidator
    : AbstractValidator<UpdateTrackerDefinitionRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateTrackerDefinitionRequestValidator"/>
    /// class and configures all validation rules for tracker updates.
    /// </summary>
    public UpdateTrackerDefinitionRequestValidator()
    {
        RuleFor(x => x.Visibility).IsInEnum();
        RuleFor(x => x.Visibility)
            .NotEqual(TrackerVisibility.RoleRestricted)
            .WithMessage(CreateTrackerDefinitionRequestValidator.RoleRestrictedMessage);
    }
}
