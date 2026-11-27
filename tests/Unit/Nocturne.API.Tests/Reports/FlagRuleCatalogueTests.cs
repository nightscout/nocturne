using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Reports;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// The flag rule catalogue: the presets and facts the editor offers, the checks a rule takes before it is saved (the
/// reports validator's), and the count of people a rule cannot be checked for, from access alone.
/// </summary>
[Trait("Category", "Unit")]
public class FlagRuleCatalogueTests
{
    private static readonly FlagRuleCatalogue Catalogue = new(ReportSpecValidatorTests.Registry, Evaluator());

    private static readonly IReadOnlySet<string> Reports = new HashSet<string> { Scope.GlucoseRead, Scope.ReportsRead };

    [Fact]
    public void Every_preset_names_a_listed_fact_and_checks()
    {
        Catalogue.Presets.Should().HaveCount(8);
        foreach (var preset in Catalogue.Presets)
        {
            Catalogue.Facts.Should().Contain(f => f.Dataset == preset.Dataset && f.Field.Name == preset.Field, preset.ToString());
            Catalogue.Check(preset.Dataset, preset.Field, preset.Days, preset.Comparison, preset.Value)
                .Should().BeEmpty(preset.ToString());
        }
    }

    [Fact]
    public void The_presets_are_the_agreed_suggestions()
    {
        Catalogue.Presets.Select(p => (p.Field, p.Days, p.Comparison, p.Value)).Should().Equal(
            ("timeBelow", 14, FlagRuleComparison.Above, 4.0),
            ("timeBelowOwnRange", 14, FlagRuleComparison.Above, 4.0),
            ("timeAbove", 14, FlagRuleComparison.Above, 25.0),
            ("timeAboveOwnRange", 14, FlagRuleComparison.Above, 25.0),
            ("sensorWornPercent", 14, FlagRuleComparison.Below, 70.0),
            ("hoursSinceReading", (int?)null, FlagRuleComparison.Above, 2.0),
            ("sensorHoursLeft", (int?)null, FlagRuleComparison.Below, 24.0),
            ("siteHoursLeft", (int?)null, FlagRuleComparison.Below, 24.0));
    }

    [Fact]
    public void The_facts_list_each_field_as_permitted_since_access_is_per_person()
    {
        Catalogue.Facts.Should().NotBeEmpty();
        Catalogue.Facts.Should().OnlyContain(f => f.Field.Permitted);
        Catalogue.Facts.Should().Contain(f => f.Field.Name == "timeBelowOwnRange" && f.Range == "own");
        Catalogue.Facts.Should().Contain(f => f.Field.Name == "timeBelow" && f.Range == "consensus");
    }

    public static TheoryData<string, string, int?, string> Refusals => new()
    {
        { "no_such_dataset", "timeBelow", 14, "dataset" },
        { "glucose_statistics", "readings", 14, "field" },
        { "glucose_statistics", "noSuchField", 14, "field" },
        { "glucose_statistics", "timeBelow", 91, "days" },
        { "glucose_statistics", "timeBelow", 0, "days" },
        { "glucose_statistics", "timeBelow", null, "days" },
        { "current_status", "hoursSinceReading", 14, "days" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_rule_the_reports_validator_refuses_is_refused_under_its_key(string dataset, string field, int? days, string key)
    {
        var errors = Catalogue.Check(dataset, field, days, FlagRuleComparison.Above, 4);

        errors.Keys.Should().Equal(key);
        errors[key].Should().NotBeEmpty();
    }

    [Fact]
    public void A_rule_cannot_be_checked_for_people_read_for_24_hours_or_missing_its_scope_and_no_names_are_given()
    {
        var reservoir = Rule("device_status", "reservoir", null);
        var timeBelow = Rule("glucose_statistics", "timeBelow", 14);
        IReadOnlyList<GlucoseReadTenant> people =
        [
            Person(Reports),
            Person(Reports, limitTo24Hours: true),
            Person(new HashSet<string> { Scope.GlucoseRead, Scope.ReportsRead, Scope.DevicesRead }),
            Person(new HashSet<string> { Scope.GlucoseRead }),
        ];

        Catalogue.CannotCheckCounts([reservoir, timeBelow], people, credentialLimitTo24Hours: false)
            .Should().Equal(3, 2);
        Catalogue.CannotCheckCounts([reservoir, timeBelow], people, credentialLimitTo24Hours: true)
            .Should().Equal(4, 4);
    }

    [Fact]
    public void A_rule_whose_fact_no_longer_checks_cannot_be_checked_for_anyone()
    {
        Catalogue.CannotCheckCounts([Rule("glucose_statistics", "removedField", 14)], [Person(Reports), Person(Reports)], false)
            .Should().Equal(2);
    }

    private static FlagRule Rule(string dataset, string field, int? days) =>
        new(Guid.NewGuid(), dataset, field, days, FlagRuleComparison.Below, 20, NotificationUrgency.Warn, DateTime.UnixEpoch, DateTime.UnixEpoch);

    private static GlucoseReadTenant Person(IReadOnlySet<string> scopes, bool limitTo24Hours = false)
    {
        var id = Guid.NewGuid();
        var tenant = new TenantRecord(id, $"p-{id:N}"[..10], "Person", true, false, false, false, null, null, null, null, null, null,
            null, null, null, null, null, DateTime.UnixEpoch, DateTime.UnixEpoch, null);
        return new GlucoseReadTenant(tenant, scopes, limitTo24Hours, scopes);
    }

    private static FlagFactEvaluator Evaluator()
    {
        var time = TimeProvider.System;
        return new FlagFactEvaluator(
            ReportSpecValidatorTests.Registry,
            new ReportSpecValidator(ReportSpecValidatorTests.Registry, time),
            new FlagFactReads(Mock.Of<IServiceScopeFactory>(), time, NullLogger<FlagFactReads>.Instance, 1),
            Mock.Of<IServiceProvider>(),
            Mock.Of<IAuditContext>(),
            new HttpContextAccessor(),
            time);
    }
}
