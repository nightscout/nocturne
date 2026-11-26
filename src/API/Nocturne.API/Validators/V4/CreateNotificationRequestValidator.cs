using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="CreateNotificationRequest"/>: each string at the length of the column it is stored in.
/// </summary>
/// <seealso cref="Controllers.V4.Monitoring.NotificationsController"/>
public class CreateNotificationRequestValidator : AbstractValidator<CreateNotificationRequest>
{
    public CreateNotificationRequestValidator()
    {
        RuleFor(x => x.Type).MaximumLength(ColumnLengths.InAppNotification.Type);
        RuleFor(x => x.Title).MaximumLength(ColumnLengths.InAppNotification.Title);
        RuleFor(x => x.Subtitle).MaximumLength(ColumnLengths.InAppNotification.Subtitle);
        RuleFor(x => x.SourceId).MaximumLength(ColumnLengths.InAppNotification.SourceId);
        RuleFor(x => x.Icon).MaximumLength(ColumnLengths.InAppNotification.Icon);
        RuleFor(x => x.Source).MaximumLength(ColumnLengths.InAppNotification.Source);
    }
}
