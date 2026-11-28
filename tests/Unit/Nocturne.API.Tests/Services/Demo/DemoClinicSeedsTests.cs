using FluentValidation.TestHelper;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Demo;
using Nocturne.API.Validators.V4;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Tests.Services.Demo;

[Trait("Category", "Unit")]
public class DemoClinicSeedsTests
{
    private static readonly Guid Persona = Guid.CreateVersion7();
    private static readonly DateTime Now = new(2026, 11, 28, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string> ViewNames() => [.. DemoClinicSeeds.Views.Select(v => v.Name)];

    public static TheoryData<int> RuleIndexes() => [.. Enumerable.Range(0, DemoClinicSeeds.Rules.Count)];

    [Theory]
    [MemberData(nameof(ViewNames))]
    public void Each_seeded_view_is_one_a_member_could_save(string name)
    {
        var view = DemoClinicSeeds.Views.Single(v => v.Name == name);

        new PeopleViewRequestValidator()
            .TestValidate(new PeopleViewRequest(view.Name, view.Query, view.Lens, view.ReviewDays))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [MemberData(nameof(RuleIndexes))]
    public void Each_seeded_rule_is_one_a_member_could_save(int index)
    {
        var rule = DemoClinicSeeds.Rules[index];

        new FlagRuleRequestValidator()
            .TestValidate(new FlagRuleRequest(rule.Dataset, rule.Field, rule.Days, rule.Comparison, rule.Value, rule.Level))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void The_seeds_fit_under_the_limits_a_member_has()
    {
        DemoClinicSeeds.Views.Count.Should().BeLessThanOrEqualTo(PeopleViewLimits.PerViewer);
        DemoClinicSeeds.Rules.Count.Should().BeLessThanOrEqualTo(FlagRuleLimits.PerViewer);
        DemoClinicSeeds.Views.Select(v => v.Name.ToUpperInvariant()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_persona_with_nothing_gets_every_seed()
    {
        var (views, rules) = Repositories([], []);

        await DemoClinicSeeds.SeedAsync(views.Object, rules.Object, Persona, CancellationToken.None);

        foreach (var view in DemoClinicSeeds.Views)
            views.Verify(v => v.CreateAsync(Persona, view, It.IsAny<CancellationToken>()), Times.Once);
        foreach (var rule in DemoClinicSeeds.Rules)
            rules.Verify(r => r.CreateAsync(Persona, rule, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_persona_holding_the_seeds_gets_nothing_more()
    {
        var (views, rules) = Repositories(
            [.. DemoClinicSeeds.Views.Select(v => Held(v with { Name = v.Name.ToUpperInvariant() }))],
            [.. DemoClinicSeeds.Rules.Select(r => Held(r with { Level = NotificationUrgency.Info }))]);

        await DemoClinicSeeds.SeedAsync(views.Object, rules.Object, Persona, CancellationToken.None);

        views.Verify(v => v.CreateAsync(It.IsAny<Guid>(), It.IsAny<PeopleViewDraft>(), It.IsAny<CancellationToken>()), Times.Never);
        rules.Verify(r => r.CreateAsync(It.IsAny<Guid>(), It.IsAny<FlagRuleDraft>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_persona_who_changed_a_seeded_rule_gets_it_again_and_keeps_theirs()
    {
        var changed = DemoClinicSeeds.Rules[0] with { Value = DemoClinicSeeds.Rules[0].Value + 1 };
        var (views, rules) = Repositories([], [Held(changed), .. DemoClinicSeeds.Rules.Skip(1).Select(Held)]);

        await DemoClinicSeeds.SeedAsync(views.Object, rules.Object, Persona, CancellationToken.None);

        rules.Verify(r => r.CreateAsync(Persona, DemoClinicSeeds.Rules[0], It.IsAny<CancellationToken>()), Times.Once);
        rules.Verify(r => r.CreateAsync(It.IsAny<Guid>(), It.IsAny<FlagRuleDraft>(), It.IsAny<CancellationToken>()), Times.Once);
        rules.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static (Mock<IPeopleViewRepository> Views, Mock<IFlagRuleRepository> Rules) Repositories(
        IReadOnlyList<PeopleView> heldViews, IReadOnlyList<FlagRule> heldRules)
    {
        var views = new Mock<IPeopleViewRepository>();
        views.Setup(v => v.GetOfSubjectAsync(Persona, It.IsAny<CancellationToken>())).ReturnsAsync(heldViews);
        var rules = new Mock<IFlagRuleRepository>();
        rules.Setup(r => r.GetOfSubjectAsync(Persona, It.IsAny<CancellationToken>())).ReturnsAsync(heldRules);
        return (views, rules);
    }

    private static PeopleView Held(PeopleViewDraft draft) =>
        new(Guid.CreateVersion7(), draft.Name, 0, draft.Query, draft.Lens, draft.ReviewDays, Now, Now);

    private static FlagRule Held(FlagRuleDraft draft) =>
        new(Guid.CreateVersion7(), draft.Dataset, draft.Field, draft.Days, draft.Comparison, draft.Value, draft.Level, Now, Now);
}
