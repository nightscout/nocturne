using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;

namespace Nocturne.API.Validators.Alerts;

/// <summary>
/// Validates <see cref="UpdateTenantAlertSettingsRequest"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>When the schedule is enabled, both start and end must be set; cross-midnight
/// windows are allowed so start &gt;= end is intentionally not rejected.</description></item>
/// <item><description><c>DndManualUntil</c> must be in the future, but only when the request is
/// actually turning manual DND on. An absent <c>DndManualActive</c> means "leave the mute alone",
/// so the controller never reads the expiry — rejecting a schedule-only save that merely echoed a
/// now-stale value back would 400 it over a field the server ignores.</description></item>
/// </list>
/// </remarks>
public class UpdateTenantAlertSettingsRequestValidator
    : AbstractValidator<UpdateTenantAlertSettingsRequest>
{
    public UpdateTenantAlertSettingsRequestValidator()
    {
        RuleFor(x => x.DndScheduleStart).NotNull()
            .When(x => x.DndScheduleEnabled)
            .WithMessage("DndScheduleStart is required when DndScheduleEnabled is true");

        RuleFor(x => x.DndScheduleEnd).NotNull()
            .When(x => x.DndScheduleEnabled)
            .WithMessage("DndScheduleEnd is required when DndScheduleEnabled is true");

        RuleFor(x => x.DndManualUntil)
            .Must(t => !t.HasValue || t.Value > DateTime.UtcNow)
            .When(x => x.DndManualActive == true)
            .WithMessage("DndManualUntil must be in the future");
    }
}
