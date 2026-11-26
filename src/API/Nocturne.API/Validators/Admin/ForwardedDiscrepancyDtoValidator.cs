using FluentValidation;
using Nocturne.API.Controllers.V4.TenantAdmin;
using Nocturne.API.Validators.V4;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Validators.Admin;

/// <summary>
/// Validates a <see cref="ForwardedDiscrepancyDto"/>: its analysis through <see cref="DiscrepancyAnalysisDtoValidator"/>,
/// and the summary as it is stored, marked with the source, at the length of its column.
/// </summary>
/// <seealso cref="DiscrepancyController"/>
public class ForwardedDiscrepancyDtoValidator : AbstractValidator<ForwardedDiscrepancyDto>
{
    public ForwardedDiscrepancyDtoValidator()
    {
        RuleFor(x => x.Analysis).SetValidator(new DiscrepancyAnalysisDtoValidator());
        RuleFor(x => DiscrepancyController.ForwardedSummary(x.SourceId, x.Analysis.Summary))
            .MaximumLength(ColumnLengths.DiscrepancyAnalysis.Summary)
            .OverridePropertyName(nameof(ForwardedDiscrepancyDto.SourceId))
            .When(x => x.Analysis is not null);
    }
}
