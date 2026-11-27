using FluentValidation;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates a <see cref="PeopleQuery"/>: a search of at most <see cref="PeopleQuery.MaxSearchLength"/> characters as
/// sent, since a view stores the query as sent; at most <see cref="PeopleQuery.MaxFilters"/> filters, each valid for its
/// kind; and a sort. Whether a fact filter's fact exists is checked against the reports engine
/// (<see cref="Reports.IFlagRuleCatalogue.CheckFacts"/>). A list over its cap is refused whole, its items not checked.
/// </summary>
public class PeopleQueryValidator : AbstractValidator<PeopleQuery>
{
    public PeopleQueryValidator()
    {
        RuleFor(x => x.Search)
            .MaximumLength(PeopleQuery.MaxSearchLength)
            .WithMessage($"Send a search of at most {PeopleQuery.MaxSearchLength} characters.");
        RuleFor(x => x.Filters)
            .Must(filters => filters is null || filters.Count <= PeopleQuery.MaxFilters)
            .WithMessage($"Send at most {PeopleQuery.MaxFilters} filters.");
        RuleForEach(x => x.Filters).NotNull().SetValidator(new PeopleFilterValidator())
            .When(x => x.Filters is null || x.Filters.Count <= PeopleQuery.MaxFilters);
        RuleFor(x => x.Sort!.Column).IsInEnum().When(x => x.Sort is not null);
        RuleFor(x => x.Sort!.Direction).IsInEnum().When(x => x.Sort is not null);
    }
}

/// <summary>
/// Validates one <see cref="PeopleFilter"/>: the fields its kind reads, and no other. A fact is validated as a flag
/// rule's is (<see cref="FlagFactFieldRules"/>).
/// </summary>
public class PeopleFilterValidator : AbstractValidator<PeopleFilter>
{
    private const string LeaveOut = "Leave this out for this kind of filter.";

    private static readonly int TherapyTypeCount = Enum.GetValues<TherapyType>().Length;

    public PeopleFilterValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();

        When(x => x.Kind == PeopleFilterKind.Fact, () => this.AddFactRules());
        When(x => x.Kind is PeopleFilterKind.AgeAtLeast or PeopleFilterKind.AgeAtMost, () =>
            RuleFor(x => x.Years).NotNull().WithMessage("Send an age in years.")
                .InclusiveBetween(0, PeopleFilter.MaxYears));
        When(x => x.Kind == PeopleFilterKind.Therapy, () =>
        {
            RuleFor(x => x.TherapyTypes).NotEmpty().WithMessage("Send one or more therapy types.")
                .Must(types => types is null || (types.Count <= TherapyTypeCount && types.Distinct().Count() == types.Count))
                .WithMessage("Send each therapy type one time.");
            RuleForEach(x => x.TherapyTypes).IsInEnum().When(x => x.TherapyTypes is null || x.TherapyTypes.Count <= TherapyTypeCount);
        });
        When(x => x.Kind == PeopleFilterKind.SourceTeam, () =>
            RuleFor(x => x.TeamId).NotNull().NotEqual(Guid.Empty).WithMessage("Send a team."));
        When(x => x.Kind == PeopleFilterKind.InGroup, () =>
            RuleFor(x => x.GroupId).NotNull().NotEqual(Guid.Empty).WithMessage("Send a group."));

        RuleFor(x => x.Dataset).Null().WithMessage(LeaveOut).When(x => x.Kind != PeopleFilterKind.Fact);
        RuleFor(x => x.Field).Null().WithMessage(LeaveOut).When(x => x.Kind != PeopleFilterKind.Fact);
        RuleFor(x => x.Days).Null().WithMessage(LeaveOut).When(x => x.Kind != PeopleFilterKind.Fact);
        RuleFor(x => x.Comparison).Null().WithMessage(LeaveOut).When(x => x.Kind != PeopleFilterKind.Fact);
        RuleFor(x => x.Value).Null().WithMessage(LeaveOut).When(x => x.Kind != PeopleFilterKind.Fact);
        RuleFor(x => x.Years).Null().WithMessage(LeaveOut)
            .When(x => x.Kind is not (PeopleFilterKind.AgeAtLeast or PeopleFilterKind.AgeAtMost));
        RuleFor(x => x.TherapyTypes).Null().WithMessage(LeaveOut).When(x => x.Kind != PeopleFilterKind.Therapy);
        RuleFor(x => x.TeamId).Null().WithMessage(LeaveOut).When(x => x.Kind != PeopleFilterKind.SourceTeam);
        RuleFor(x => x.GroupId).Null().WithMessage(LeaveOut).When(x => x.Kind != PeopleFilterKind.InGroup);
    }
}

/// <summary>Validates <see cref="PeopleOverviewRequest"/>: its rules and its query.</summary>
public class PeopleOverviewRequestValidator : AbstractValidator<PeopleOverviewRequest>
{
    public PeopleOverviewRequestValidator()
    {
        RuleFor(x => x.Rules).IsInEnum();
        RuleFor(x => x.Query!).SetValidator(new PeopleQueryValidator()).When(x => x.Query is not null);
    }
}

/// <summary>
/// Validates <see cref="PeopleReviewRequest"/>: its rules and its query. The period is checked by the read, which
/// states the periods it takes.
/// </summary>
public class PeopleReviewRequestValidator : AbstractValidator<PeopleReviewRequest>
{
    public PeopleReviewRequestValidator()
    {
        RuleFor(x => x.Rules).IsInEnum();
        RuleFor(x => x.Query!).SetValidator(new PeopleQueryValidator()).When(x => x.Query is not null);
    }
}

/// <summary>
/// Validates <see cref="PeopleViewRequest"/>: a name that is not blank, of at most <see cref="PeopleViewLimits.NameLength"/>
/// characters, a lens, a review period only for the Review lens and then one of <see cref="TenantReviewPeriods.Days"/>, and
/// a query whose sort is by name or by a column of the view's lens.
/// </summary>
public class PeopleViewRequestValidator : AbstractValidator<PeopleViewRequest>
{
    public PeopleViewRequestValidator()
    {
        RuleFor(x => x.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("Send a name.")
            .MaximumLength(PeopleViewLimits.NameLength)
            .WithMessage($"Send a name of at most {PeopleViewLimits.NameLength} characters.");
        RuleFor(x => x.Lens).NotNull().WithMessage("Send a lens: now or review.").IsInEnum();
        RuleFor(x => x.ReviewDays)
            .Must(days => days is { } d && TenantReviewPeriods.IsSupported(d))
            .WithMessage($"Send a period of {string.Join(", ", TenantReviewPeriods.Days)} days.")
            .When(x => x.Lens == PeopleLens.Review);
        RuleFor(x => x.ReviewDays)
            .Null().WithMessage("Leave out the period for the now lens.")
            .When(x => x.Lens == PeopleLens.Now);
        RuleFor(x => x.Query!).SetValidator(new PeopleQueryValidator()).When(x => x.Query is not null);
        RuleFor(x => x.Query!.Sort!.Column)
            .Must((request, column) => SortFits(column, request.Lens))
            .WithMessage("Sort by name or by a column of the view's lens.")
            .When(x => x.Query?.Sort is not null && x.Lens is not null);
    }

    private static bool SortFits(PeopleSortColumn column, PeopleLens? lens) =>
        column == PeopleSortColumn.Name
        || (lens == PeopleLens.Now ? PeopleSort.NowColumns : PeopleSort.ReviewColumns).Contains(column);
}
