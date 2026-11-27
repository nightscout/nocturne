using FluentValidation;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="CreateStateSpanRequest"/> for the V4 state span endpoint, capping State, Source
/// and OriginalId at the state span columns they are stored in.
/// </summary>
/// <seealso cref="StateSpansController"/>
public class CreateStateSpanRequestValidator : AbstractValidator<CreateStateSpanRequest>
{
    public CreateStateSpanRequestValidator()
    {
        RuleFor(x => x.State).MaximumLength(ColumnLengths.StateSpan.State);
        RuleFor(x => x.Source).MaximumLength(ColumnLengths.StateSpan.Source);
        RuleFor(x => x.OriginalId).MaximumLength(ColumnLengths.StateSpan.LegacyId);
    }
}
