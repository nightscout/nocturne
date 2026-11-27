using FluentAssertions;
using Moq;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Xunit;
using static Nocturne.API.Tests.Services.Identity.TenantOverviewTestKit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The caller's own flag rules in the overview: what the overview does with the reasons a run gives. What a rule
/// judges is <c>FlagFactEvaluatorTests</c> and <c>PersonalFlagRulesTests</c>.
/// </summary>
public partial class TenantOverviewServiceTests
{
    [Fact]
    public async Task A_rule_reason_flags_a_quiet_person_and_the_api_orders_them_ahead_of_people_without_one()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var calm = SeedMembership(options, subjectId, "calm", [Scope.GlucoseRead]);
        var ruled = SeedMembership(options, subjectId, "ruled", [Scope.GlucoseRead]);
        var fakes = QuietFakes(calm, ruled);
        var rules = new ScriptedRules { Reasons = { [ruled] = [RuleReason(NotificationUrgency.Warn, null)] } };

        var response = await NewService(options, fakes: fakes, flagRules: rules)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, OverviewRuleSet.All, PeopleQuery.Empty);

        response.Tenants.Select(t => t.Slug).Should().Equal("ruled", "calm");
        var reason = response.Tenants[0].Attention.Should().ContainSingle().Subject;
        reason.Kind.Should().Be(TenantOverviewAttentionKind.FlagRule);
        reason.Rule.Should().NotBeNull();
        reason.Rule!.Source.Should().Be(FlagRuleSource.Yours);
        response.Tenants[1].Attention.Should().BeEmpty();
    }

    [Fact]
    public async Task Rule_reasons_are_merged_with_the_fixed_set_by_level_most_severe_first()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var low = SeedMembership(options, subjectId, "low", [Scope.GlucoseRead]);
        var fakes = new Fakes();
        fakes.Data[low] = new TenantData { Latest = new SensorGlucose { Mgdl = 60, Timestamp = DateTime.UtcNow.AddMinutes(-1) } };
        var rules = new ScriptedRules
        {
            Reasons = { [low] = [RuleReason(NotificationUrgency.Info, null), RuleReason(NotificationUrgency.Hazard, null)] },
        };

        var response = await NewService(options, fakes: fakes, flagRules: rules)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, OverviewRuleSet.All, PeopleQuery.Empty);

        var attention = response.Tenants.Single().Attention;
        attention.Should().HaveCount(3);
        attention.Select(r => r.Level).Should().BeInDescendingOrder();
        attention.Should().Contain(r => r.Kind != TenantOverviewAttentionKind.FlagRule, "the fixed set is kept");
        attention[^1].Should().Be(rules.Reasons[low][0]);
    }

    [Fact]
    public async Task A_read_of_the_fixed_set_never_loads_rules_and_names_no_team()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var ruled = SeedMembership(options, subjectId, "ruled", [Scope.GlucoseRead]);
        var rules = new ScriptedRules
        {
            Pending = 3,
            Reasons = { [ruled] = [RuleReason(NotificationUrgency.Hazard, null)] },
            Teams = [new TenantOverviewRuleTeam(Guid.NewGuid(), "Ward 4")],
        };

        var response = await NewService(options, fakes: QuietFakes(ruled), flagRules: rules)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, OverviewRuleSet.Fixed, PeopleQuery.Empty);

        rules.Begun.Should().Be(0);
        response.Tenants.Single().Attention.Should().BeEmpty();
        response.PeopleWithRulesPending.Should().Be(0);
        response.RuleTeams.Should().BeEmpty();
    }

    [Theory]
    [InlineData(OverviewRuleSet.Team)]
    [InlineData(OverviewRuleSet.All)]
    public async Task A_read_of_team_or_all_rules_passes_its_set_and_people_on_and_names_the_teams_whose_rules_applied(OverviewRuleSet set)
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var first = SeedMembership(options, subjectId, "first", [Scope.GlucoseRead]);
        var second = SeedMembership(options, subjectId, "second", [Scope.GlucoseRead]);
        var ward = new TenantOverviewRuleTeam(Guid.NewGuid(), "Ward 4");
        var rules = new ScriptedRules
        {
            Reasons = { [first] = [RuleReason(NotificationUrgency.Warn, ward)] },
            Teams = [ward],
        };

        var response = await NewService(options, fakes: QuietFakes(first, second), flagRules: rules)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, set, PeopleQuery.Empty);

        rules.RuleSets.Should().Equal(set);
        rules.PeopleBegunFor.Single().Should().BeEquivalentTo([first, second]);
        response.RuleTeams.Should().Equal(ward);
        response.Tenants.Single(t => t.TenantId == first).Attention.Single().Rule!.Team.Should().Be(ward);
    }

    [Fact]
    public async Task The_response_names_the_people_still_pending_once_every_person_is_built()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var first = SeedMembership(options, subjectId, "first", [Scope.GlucoseRead]);
        var second = SeedMembership(options, subjectId, "second", [Scope.GlucoseRead]);
        var rules = new ScriptedRules { Pending = 2 };

        var response = await NewService(options, fakes: QuietFakes(first, second), flagRules: rules)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, OverviewRuleSet.All, PeopleQuery.Empty);

        response.PeopleWithRulesPending.Should().Be(2);
        rules.Judged.Should().HaveCount(2);
        rules.FinishedAfter.Should().Be(2, "the background reads start once, after every person is judged");
        response.Tenants.Should().OnlyContain(t => t.Attention.Count == 0, "a pending person carries the fixed set only");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_credential_24_hour_limit_is_passed_to_the_rules_as_it_is(bool limited)
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var person = SeedMembership(options, subjectId, "person", [Scope.GlucoseRead]);
        var rules = new ScriptedRules();

        await NewService(options, fakes: QuietFakes(person), flagRules: rules)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, OverviewRuleSet.All, PeopleQuery.Empty, limited);

        rules.CredentialLimits.Should().Equal(limited);
        rules.Subjects.Should().Equal(subjectId);
    }

    [Fact]
    public async Task Rules_are_judged_on_the_latest_reading_trackers_and_pump_the_overview_read_with_no_second_read()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var person = SeedMembership(options, subjectId, "person", [Scope.GlucoseRead, Scope.AlertsRead, Scope.DevicesRead]);
        var latest = new SensorGlucose { Mgdl = 120, Timestamp = DateTime.UtcNow.AddMinutes(-1) };
        var pump = new PumpSnapshot { Timestamp = DateTime.UtcNow.AddMinutes(-4), Reservoir = 12, BatteryPercent = 40 };
        var fakes = new Fakes();
        fakes.Data[person] = new TenantData
        {
            Latest = latest,
            Trackers = [EventTracker("sensor", hoursUntil: 40, NotificationUrgency.Warn)],
            Pump = pump,
        };
        var rules = new ScriptedRules();

        await NewService(options, fakes: fakes, flagRules: rules)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, OverviewRuleSet.All, PeopleQuery.Empty);

        var judged = rules.Judged.Should().ContainSingle().Subject;
        judged.Person.Tenant.Id.Should().Be(person);
        judged.Latest.Should().BeSameAs(latest);
        judged.Trackers.Should().ContainSingle().Which.Definition.Name.Should().Be("sensor");
        judged.Pump.Should().BeSameAs(pump);
        fakes.Canonical.Verify(c => c.GetLatestAsync(It.IsAny<CancellationToken>()), Times.Once);
        fakes.Trackers.Verify(t => t.GetActiveInstancesAsync(It.IsAny<CancellationToken>()), Times.Once);
        fakes.Pumps.Verify(p => p.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_caller_with_no_rules_gets_the_fixed_set_and_nothing_pending()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "person", [Scope.GlucoseRead]);
        var rules = new ScriptedRules { None = true };

        var response = await NewService(options, flagRules: rules)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, OverviewRuleSet.All, PeopleQuery.Empty);

        rules.Begun.Should().Be(1);
        response.PeopleWithRulesPending.Should().Be(0);
    }

    [Fact]
    public async Task A_caller_following_no_one_loads_no_rules()
    {
        var rules = new ScriptedRules();

        var response = await NewService(NewOptions(), flagRules: rules)
            .GetOverviewAsync(Guid.NewGuid(), FullTokenScopes, AuthType.SessionCookie, OverviewRuleSet.All, PeopleQuery.Empty);

        response.Tenants.Should().BeEmpty();
        rules.Begun.Should().Be(0);
    }

    private static Fakes QuietFakes(params Guid[] tenants)
    {
        var fakes = new Fakes();
        foreach (var tenant in tenants)
            fakes.Data[tenant] = new TenantData { Latest = new SensorGlucose { Mgdl = 120, Timestamp = DateTime.UtcNow.AddMinutes(-1) } };
        return fakes;
    }
}
