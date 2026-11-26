using FluentValidation;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="CreateSystemEventRequest"/>: each string at the length of the column it is stored in.
/// </summary>
/// <seealso cref="Controllers.V4.Platform.SystemEventsController"/>
public class CreateSystemEventRequestValidator : AbstractValidator<CreateSystemEventRequest>
{
    public CreateSystemEventRequestValidator()
    {
        RuleFor(x => x.Code).MaximumLength(ColumnLengths.SystemEvent.Code);
        RuleFor(x => x.Description).MaximumLength(ColumnLengths.SystemEvent.Description);
        RuleFor(x => x.Source).MaximumLength(ColumnLengths.SystemEvent.Source);
        RuleFor(x => x.OriginalId).MaximumLength(ColumnLengths.SystemEvent.OriginalId);
    }
}
