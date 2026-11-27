using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates a forwarded <see cref="DiscrepancyAnalysisDto"/>: each string at the length of the column it is stored
/// in, and each discrepancy through <see cref="DiscrepancyDetailDtoValidator"/>.
/// </summary>
/// <seealso cref="Admin.ForwardedDiscrepancyDtoValidator"/>
public class DiscrepancyAnalysisDtoValidator : AbstractValidator<DiscrepancyAnalysisDto>
{
    public DiscrepancyAnalysisDtoValidator()
    {
        RuleFor(x => x.TraceId).MaximumLength(ColumnLengths.DiscrepancyAnalysis.TraceId);
        RuleFor(x => x.RequestMethod).MaximumLength(ColumnLengths.DiscrepancyAnalysis.RequestMethod);
        RuleFor(x => x.RequestPath).MaximumLength(ColumnLengths.DiscrepancyAnalysis.RequestPath);
        RuleFor(x => x.Summary).MaximumLength(ColumnLengths.DiscrepancyAnalysis.Summary);
        RuleFor(x => x.SelectedResponseTarget).MaximumLength(ColumnLengths.DiscrepancyAnalysis.SelectedResponseTarget);
        RuleFor(x => x.SelectionReason).MaximumLength(ColumnLengths.DiscrepancyAnalysis.SelectionReason);
        RuleFor(x => x.ErrorMessage).MaximumLength(ColumnLengths.DiscrepancyAnalysis.ErrorMessage);
        RuleForEach(x => x.Discrepancies).SetValidator(new DiscrepancyDetailDtoValidator());
    }
}
