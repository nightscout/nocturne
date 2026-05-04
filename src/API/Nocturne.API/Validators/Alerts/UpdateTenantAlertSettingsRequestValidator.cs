using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;

namespace Nocturne.API.Validators.Alerts;

/// <summary>
/// Validates <see cref="UpdateTenantAlertSettingsRequest"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Timezone is required and capped at 64 characters (matches the column).</description></item>
/// <item><description>When the schedule is enabled, both start and end must be set; cross-midnight
/// windows are allowed so start &gt;= end is intentionally not rejected.</description></item>
/// <item><description><c>DndManualUntil</c>, when present, must be in the future.</description></item>
/// </list>
/// </remarks>
public class UpdateTenantAlertSettingsRequestValidator
    : AbstractValidator<UpdateTenantAlertSettingsRequest>
{
    public UpdateTenantAlertSettingsRequestValidator()
    {
        RuleFor(x => x.Timezone).NotEmpty().MaximumLength(64);

        RuleFor(x => x.DndScheduleStart).NotNull()
            .When(x => x.DndScheduleEnabled)
            .WithMessage("DndScheduleStart is required when DndScheduleEnabled is true");

        RuleFor(x => x.DndScheduleEnd).NotNull()
            .When(x => x.DndScheduleEnabled)
            .WithMessage("DndScheduleEnd is required when DndScheduleEnabled is true");

        RuleFor(x => x.DndManualUntil)
            .Must(t => !t.HasValue || t.Value > DateTime.UtcNow)
            .WithMessage("DndManualUntil must be in the future");
    }
}
