using FluentValidation;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="PageViewRequest"/>: a route id no longer than the <c>page_view_counts</c>
/// column. The controller also refuses any id that is not a page of the app.
/// </summary>
public class PageViewRequestValidator : AbstractValidator<PageViewRequest>
{
    public PageViewRequestValidator()
    {
        RuleFor(x => x.RouteId).NotEmpty().MaximumLength(ColumnLengths.PageViewCount.RouteId);
    }
}
