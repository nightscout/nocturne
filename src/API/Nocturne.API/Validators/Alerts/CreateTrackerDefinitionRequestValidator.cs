using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Models;

namespace Nocturne.API.Validators.Alerts;

/// <summary>
/// Validates <see cref="CreateTrackerDefinitionRequest"/> for the V4 tracker creation endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Visibility must be a valid <see cref="TrackerVisibility"/> value.</description></item>
/// <item><description>Visibility must not be <see cref="TrackerVisibility.RoleRestricted"/>, which has no
/// view rule behind it: <c>TrackersController.CanViewTracker</c> grants only the owner and admins, and
/// the tracker carries no roles to match a viewer against.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="CreateTrackerDefinitionRequest"/>
/// <seealso cref="TrackersController"/>
public class CreateTrackerDefinitionRequestValidator
    : AbstractValidator<CreateTrackerDefinitionRequest>
{
    /// <summary>
    /// Rejection message for a write that asks for RoleRestricted visibility, shared with
    /// <see cref="UpdateTrackerDefinitionRequestValidator"/>.
    /// </summary>
    public const string RoleRestrictedMessage =
        "RoleRestricted visibility is not implemented; use Public or Private";

    /// <summary>
    /// Initializes a new instance of the <see cref="CreateTrackerDefinitionRequestValidator"/>
    /// class and configures all validation rules for tracker creation.
    /// </summary>
    public CreateTrackerDefinitionRequestValidator()
    {
        RuleFor(x => x.Visibility).IsInEnum();
        RuleFor(x => x.Visibility)
            .NotEqual(TrackerVisibility.RoleRestricted)
            .WithMessage(RoleRestrictedMessage);
    }
}
