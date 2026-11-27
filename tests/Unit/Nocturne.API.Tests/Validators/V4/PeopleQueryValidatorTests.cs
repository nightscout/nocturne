using FluentAssertions;
using FluentValidation.TestHelper;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Validators.V4;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Validators.V4;

[Trait("Category", "Unit")]
public class PeopleQueryValidatorTests
{
    private readonly PeopleOverviewRequestValidator _overview = new();
    private readonly PeopleReviewRequestValidator _review = new();
    private readonly PeopleViewRequestValidator _view = new();

    private static PeopleFilter Filter(PeopleFilterKind kind) => new(kind, null, null, null, null, null, null, null, null, null);

    private static PeopleFilter Fact => Filter(PeopleFilterKind.Fact) with
    {
        Dataset = "glucose_statistics", Field = "timeBelow", Days = 14, Comparison = FlagRuleComparison.Above, Value = 4,
    };

    public static TheoryData<PeopleFilter> ValidFilters => new()
    {
        Fact,
        Filter(PeopleFilterKind.AgeAtLeast) with { Years = 0 },
        Filter(PeopleFilterKind.AgeAtMost) with { Years = PeopleFilter.MaxYears },
        Filter(PeopleFilterKind.Therapy) with { TherapyTypes = [TherapyType.Pump, TherapyType.AutomatedDelivery] },
        Filter(PeopleFilterKind.Flagged),
        Filter(PeopleFilterKind.SourceDirect),
        Filter(PeopleFilterKind.SourceTeam) with { TeamId = Guid.NewGuid() },
        Filter(PeopleFilterKind.InGroup) with { GroupId = Guid.NewGuid() },
    };

    public static TheoryData<PeopleFilter, string> RefusedFilters => new()
    {
        { Fact with { Dataset = null }, "Dataset" },
        { Fact with { Field = new string('f', 65) }, "Field" },
        { Fact with { Comparison = null }, "Comparison" },
        { Fact with { Value = double.NaN }, "Value" },
        { Fact with { Years = 3 }, "Years" },
        { Filter(PeopleFilterKind.AgeAtLeast), "Years" },
        { Filter(PeopleFilterKind.AgeAtMost) with { Years = -1 }, "Years" },
        { Filter(PeopleFilterKind.AgeAtLeast) with { Years = PeopleFilter.MaxYears + 1 }, "Years" },
        { Filter(PeopleFilterKind.AgeAtLeast) with { Years = 18, Value = 4 }, "Value" },
        { Filter(PeopleFilterKind.Therapy), "TherapyTypes" },
        { Filter(PeopleFilterKind.Therapy) with { TherapyTypes = [] }, "TherapyTypes" },
        { Filter(PeopleFilterKind.Therapy) with { TherapyTypes = [(TherapyType)9] }, "TherapyTypes[0]" },
        { Filter(PeopleFilterKind.Therapy) with { TherapyTypes = [TherapyType.Pump, TherapyType.Pump] }, "TherapyTypes" },
        {
            Filter(PeopleFilterKind.Therapy) with { TherapyTypes = [.. Enum.GetValues<TherapyType>(), TherapyType.Pump] },
            "TherapyTypes"
        },
        { Filter(PeopleFilterKind.Flagged) with { GroupId = Guid.NewGuid() }, "GroupId" },
        { Filter(PeopleFilterKind.SourceDirect) with { TeamId = Guid.NewGuid() }, "TeamId" },
        { Filter(PeopleFilterKind.SourceTeam), "TeamId" },
        { Filter(PeopleFilterKind.SourceTeam) with { TeamId = Guid.Empty }, "TeamId" },
        { Filter(PeopleFilterKind.InGroup), "GroupId" },
        { Filter(PeopleFilterKind.InGroup) with { GroupId = Guid.NewGuid(), Dataset = "glucose_statistics" }, "Dataset" },
        { Filter((PeopleFilterKind)42), "Kind" },
    };

    [Theory]
    [MemberData(nameof(ValidFilters))]
    public void A_filter_with_the_fields_of_its_kind_passes(PeopleFilter filter) =>
        _overview.TestValidate(new PeopleOverviewRequest(null, null, new PeopleQuery(null, [filter], null)))
            .ShouldNotHaveAnyValidationErrors();

    [Theory]
    [MemberData(nameof(RefusedFilters))]
    public void A_filter_missing_a_field_of_its_kind_or_holding_another_kinds_is_refused(PeopleFilter filter, string field)
    {
        var query = new PeopleQuery(null, [filter], null);

        _overview.TestValidate(new PeopleOverviewRequest(null, null, query))
            .ShouldHaveValidationErrorFor($"Query.Filters[0].{field}");
        _review.TestValidate(new PeopleReviewRequest(null, 14, null, query))
            .ShouldHaveValidationErrorFor($"Query.Filters[0].{field}");
    }

    [Fact]
    public void A_search_longer_than_the_limit_as_sent_or_too_many_filters_are_refused()
    {
        var longest = new string('a', PeopleQuery.MaxSearchLength);

        _overview.TestValidate(new PeopleOverviewRequest(null, null, new PeopleQuery(longest, null, null)))
            .ShouldNotHaveAnyValidationErrors();
        _overview.TestValidate(new PeopleOverviewRequest(null, null, new PeopleQuery(longest + "a", null, null)))
            .ShouldHaveValidationErrorFor("Query.Search");
        _view.TestValidate(new PeopleViewRequest("Padded", new PeopleQuery($" {longest}", null, null), PeopleLens.Now, null))
            .ShouldHaveValidationErrorFor("Query.Search");
        var tooMany = _overview.TestValidate(new PeopleOverviewRequest(null, null,
            new PeopleQuery(null, [.. Enumerable.Repeat(Filter((PeopleFilterKind)42), PeopleQuery.MaxFilters + 1)], null)));
        tooMany.ShouldHaveValidationErrorFor("Query.Filters");
        tooMany.Errors.Should().ContainSingle("a list over its cap is refused whole, its items not checked");
    }

    [Fact]
    public void An_unknown_rule_set_or_sort_is_refused()
    {
        _overview.TestValidate(new PeopleOverviewRequest(null, (OverviewRuleSet)7, null)).ShouldHaveValidationErrorFor(x => x.Rules);
        _overview.TestValidate(new PeopleOverviewRequest(null, null,
                new PeopleQuery(null, null, new PeopleSort((PeopleSortColumn)99, PeopleSortDirection.Ascending))))
            .ShouldHaveValidationErrorFor("Query.Sort.Column");
        _review.TestValidate(new PeopleReviewRequest(null, 14, null,
                new PeopleQuery(null, null, new PeopleSort(PeopleSortColumn.Name, (PeopleSortDirection)5))))
            .ShouldHaveValidationErrorFor("Query.Sort.Direction");
    }

    [Theory]
    [InlineData(PeopleLens.Now, null)]
    [InlineData(PeopleLens.Review, 14)]
    [InlineData(PeopleLens.Review, 30)]
    [InlineData(PeopleLens.Review, 90)]
    public void A_view_with_a_lens_and_the_period_it_takes_passes(PeopleLens lens, int? days) =>
        _view.TestValidate(new PeopleViewRequest("Night shift", null, lens, days)).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData(PeopleLens.Now, 14)]
    [InlineData(PeopleLens.Review, null)]
    [InlineData(PeopleLens.Review, 7)]
    public void A_view_with_a_period_its_lens_does_not_take_is_refused(PeopleLens lens, int? days) =>
        _view.TestValidate(new PeopleViewRequest("Night shift", null, lens, days)).ShouldHaveValidationErrorFor(x => x.ReviewDays);

    [Fact]
    public void A_view_without_a_name_or_a_lens_or_with_an_overlong_name_is_refused()
    {
        _view.TestValidate(new PeopleViewRequest("   ", null, PeopleLens.Now, null)).ShouldHaveValidationErrorFor(x => x.Name);
        _view.TestValidate(new PeopleViewRequest(new string('n', PeopleViewLimits.NameLength + 1), null, PeopleLens.Now, null))
            .ShouldHaveValidationErrorFor(x => x.Name);
        _view.TestValidate(new PeopleViewRequest(new string('n', PeopleViewLimits.NameLength), null, PeopleLens.Now, null))
            .ShouldNotHaveAnyValidationErrors();
        _view.TestValidate(new PeopleViewRequest("Night shift", null, null, null)).ShouldHaveValidationErrorFor(x => x.Lens);
    }

    [Theory]
    [InlineData(PeopleLens.Now, PeopleSortColumn.Glucose, true)]
    [InlineData(PeopleLens.Now, PeopleSortColumn.Name, true)]
    [InlineData(PeopleLens.Now, PeopleSortColumn.TimeInRange, false)]
    [InlineData(PeopleLens.Review, PeopleSortColumn.Gmi, true)]
    [InlineData(PeopleLens.Review, PeopleSortColumn.Name, true)]
    [InlineData(PeopleLens.Review, PeopleSortColumn.ReadingAge, false)]
    public void A_view_sorts_by_name_or_a_column_of_its_lens(PeopleLens lens, PeopleSortColumn column, bool passes)
    {
        var result = _view.TestValidate(new PeopleViewRequest(
            "Night shift",
            new PeopleQuery(null, null, new PeopleSort(column, PeopleSortDirection.Ascending)),
            lens,
            lens == PeopleLens.Review ? 14 : null));

        if (passes)
            result.ShouldNotHaveAnyValidationErrors();
        else
            result.ShouldHaveValidationErrorFor("Query.Sort.Column");
    }

    [Fact]
    public void A_view_validates_its_query()
    {
        _view.TestValidate(new PeopleViewRequest("Night shift", new PeopleQuery(null, [Filter(PeopleFilterKind.InGroup)], null), PeopleLens.Now, null))
            .ShouldHaveValidationErrorFor("Query.Filters[0].GroupId");
    }
}
