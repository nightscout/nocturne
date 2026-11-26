using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertActivityRequest"/> for the V4 activity endpoint. An activity is stored as a
/// state span, so Type is capped at the state column and EnteredBy at the source column.
/// </summary>
/// <seealso cref="Controllers.V4.Health.ActivityController"/>
public class UpsertActivityRequestValidator : AbstractValidator<UpsertActivityRequest>
{
    public UpsertActivityRequestValidator()
    {
        RuleFor(x => x.Type).MaximumLength(ColumnLengths.StateSpan.State);
        RuleFor(x => x.EnteredBy).MaximumLength(ColumnLengths.StateSpan.Source);
    }
}
