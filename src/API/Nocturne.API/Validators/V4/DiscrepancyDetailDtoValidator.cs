using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates a <see cref="DiscrepancyDetailDto"/> of a forwarded analysis: each string at the length of the column
/// it is stored in.
/// </summary>
/// <seealso cref="DiscrepancyAnalysisDtoValidator"/>
public class DiscrepancyDetailDtoValidator : AbstractValidator<DiscrepancyDetailDto>
{
    public DiscrepancyDetailDtoValidator()
    {
        RuleFor(x => x.Field).MaximumLength(ColumnLengths.DiscrepancyDetail.Field);
        RuleFor(x => x.NightscoutValue).MaximumLength(ColumnLengths.DiscrepancyDetail.NightscoutValue);
        RuleFor(x => x.NocturneValue).MaximumLength(ColumnLengths.DiscrepancyDetail.NocturneValue);
        RuleFor(x => x.Description).MaximumLength(ColumnLengths.DiscrepancyDetail.Description);
    }
}
