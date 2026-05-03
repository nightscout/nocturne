using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;

namespace Nocturne.API.Validators.Alerts;

/// <summary>
/// Validates <see cref="CreateAlertInviteRequest"/> for the V4 alert invite creation endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>AlertRuleChannelId must be a non-empty GUID.</description></item>
/// <item><description>PermissionScope, when provided, is capped at 100 characters.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="CreateAlertInviteRequest"/>
/// <seealso cref="AlertInvitesController"/>
public class CreateAlertInviteRequestValidator : AbstractValidator<CreateAlertInviteRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CreateAlertInviteRequestValidator"/> class
    /// and configures all validation rules for alert invite creation.
    /// </summary>
    public CreateAlertInviteRequestValidator()
    {
        RuleFor(x => x.AlertRuleChannelId).NotEmpty();
        RuleFor(x => x.PermissionScope).MaximumLength(100).When(x => x.PermissionScope is not null);
    }
}
