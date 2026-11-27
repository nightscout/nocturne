using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;
using static Nocturne.API.Tests.Services.Identity.TenantOverviewTestKit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The overview read of a <see cref="PeopleQuery"/>: the search and the person facts narrow the people, the fact filters
/// keep what the judging matched, and the flagged filter what the read flagged. How a fact filter is judged is
/// <c>FlagFactEvaluatorTests</c>; where a person comes from and groups are <c>TeamAccessEndPostgresTests</c>.
/// </summary>
public partial class TenantOverviewServiceTests
{
    private static readonly List<string> RecordReader = [Scope.GlucoseRead, Scope.TherapyRead, Scope.DevicesRead];

    private static PeopleFilter Filter(PeopleFilterKind kind) =>
        new(kind, null, null, null, null, null, null, null, null, null);

    private static PeopleFilter FactFilter(double value = 4) =>
        Filter(PeopleFilterKind.Fact) with
        {
            Dataset = "glucose_statistics", Field = "timeBelow", Days = 14, Comparison = FlagRuleComparison.Above, Value = value,
        };

    private static PeopleQuery Filtered(params PeopleFilter[] filters) => new(null, filters, null);

    private static Task<TenantOverviewResponse> ReadAsync(
        TenantOverviewService service, Guid subjectId, PeopleQuery query, OverviewRuleSet rules = OverviewRuleSet.All) =>
        service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, rules, query);

    [Fact]
    public async Task A_search_keeps_the_people_whose_name_holds_it_ignoring_case_and_the_spaces_around_it()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var alice = SeedMembership(options, subjectId, "Alice", [Scope.GlucoseRead]);
        var malia = SeedMembership(options, subjectId, "malia", [Scope.GlucoseRead]);
        SeedMembership(options, subjectId, "bob", [Scope.GlucoseRead]);
        var rules = new ScriptedRules();

        var response = await ReadAsync(
            NewService(options, fakes: QuietFakes(), flagRules: rules), subjectId, new PeopleQuery("  ALI ", null, null));

        response.Tenants.Select(t => t.TenantId).Should().BeEquivalentTo([alice, malia]);
        rules.PeopleBegunFor.Single().Should().BeEquivalentTo([alice, malia], "no figure of anyone else is judged for the query");
    }

    [Fact]
    public async Task An_age_filter_keeps_the_people_of_that_age_and_none_whose_record_does_not_say_or_who_the_caller_may_not_read_it_for()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var adult = SeedMembership(options, subjectId, "adult", RecordReader);
        SeedRecord(options, adult, today.AddYears(-30));
        var child = SeedMembership(options, subjectId, "child", RecordReader);
        SeedRecord(options, child, today.AddYears(-10));
        var eighteenTomorrow = SeedMembership(options, subjectId, "eighteen-tomorrow", RecordReader);
        SeedRecord(options, eighteenTomorrow, today.AddYears(-18).AddDays(1));
        var unknown = SeedMembership(options, subjectId, "unknown", RecordReader);
        SeedRecord(options, unknown, null);
        var noRecord = SeedMembership(options, subjectId, "no-record", RecordReader);
        var unreadable = SeedMembership(options, subjectId, "unreadable", [Scope.GlucoseRead]);
        SeedRecord(options, unreadable, today.AddYears(-40));
        var service = NewService(options, fakes: QuietFakes(adult, child, eighteenTomorrow, unknown, noRecord, unreadable));

        var grown = await ReadAsync(service, subjectId, Filtered(Filter(PeopleFilterKind.AgeAtLeast) with { Years = 18 }));
        var young = await ReadAsync(service, subjectId, Filtered(Filter(PeopleFilterKind.AgeAtMost) with { Years = 17 }));

        grown.Tenants.Select(t => t.TenantId).Should().Equal(adult);
        young.Tenants.Select(t => t.TenantId).Should().BeEquivalentTo([child, eighteenTomorrow]);
    }

    [Fact]
    public async Task A_therapy_filter_keeps_the_people_of_those_therapies_and_none_whose_record_does_not_say_or_who_the_caller_may_not_read_devices_for()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var automated = SeedMembership(options, subjectId, "automated", RecordReader);
        SeedDevice(options, automated, DeviceCategory.InsulinPump, AidAlgorithm.Loop);
        var pump = SeedMembership(options, subjectId, "pump", RecordReader);
        SeedDevice(options, pump, DeviceCategory.InsulinPump, AidAlgorithm.None);
        var injections = SeedMembership(options, subjectId, "injections", RecordReader);
        SeedInsulin(options, injections);
        var noInsulin = SeedMembership(options, subjectId, "no-insulin", RecordReader);
        SeedRecord(options, noInsulin, null, takesNoInsulin: true);
        var unsaid = SeedMembership(options, subjectId, "unsaid", RecordReader);
        SeedRecord(options, unsaid, null);
        var noDevices = SeedMembership(options, subjectId, "no-devices", [Scope.GlucoseRead, Scope.TherapyRead]);
        SeedDevice(options, noDevices, DeviceCategory.InsulinPump, AidAlgorithm.None);
        var service = NewService(options, fakes: QuietFakes(automated, pump, injections, noInsulin, unsaid, noDevices));

        async Task<IEnumerable<Guid>> Of(params TherapyType[] types) =>
            (await ReadAsync(service, subjectId, Filtered(Filter(PeopleFilterKind.Therapy) with { TherapyTypes = types })))
            .Tenants.Select(t => t.TenantId);

        (await Of(TherapyType.AutomatedDelivery)).Should().Equal(automated);
        (await Of(TherapyType.Pump)).Should().Equal(pump);
        (await Of(TherapyType.Injections)).Should().Equal(injections);
        (await Of(TherapyType.NoInsulin)).Should().Equal(noInsulin);
        (await Of(TherapyType.Pump, TherapyType.Injections)).Should().BeEquivalentTo([pump, injections]);
        (await Of(Enum.GetValues<TherapyType>())).Should().NotContain([unsaid, noDevices]);
    }

    [Fact]
    public async Task A_fact_filter_keeps_the_people_the_judging_matched_and_counts_the_people_still_pending()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var matched = SeedMembership(options, subjectId, "matched", [Scope.GlucoseRead]);
        var unmatched = SeedMembership(options, subjectId, "unmatched", [Scope.GlucoseRead]);
        var fact = FactFilter();
        var rules = new ScriptedRules { Pending = 1, FailingFilters = { unmatched } };

        var response = await ReadAsync(
            NewService(options, fakes: QuietFakes(matched, unmatched), flagRules: rules), subjectId,
            Filtered(fact, Filter(PeopleFilterKind.SourceDirect)), OverviewRuleSet.Fixed);

        response.Tenants.Select(t => t.TenantId).Should().Equal(matched);
        response.PeopleWithRulesPending.Should().Be(1);
        rules.RuleSets.Should().Equal(OverviewRuleSet.Fixed);
        rules.FactsBegunFor.Single().Should().Equal(fact);
    }

    [Theory]
    [InlineData(OverviewRuleSet.Fixed, new[] { "stale" })]
    [InlineData(OverviewRuleSet.Team, new[] { "ruled", "stale" })]
    [InlineData(OverviewRuleSet.All, new[] { "ruled", "stale" })]
    public async Task The_flagged_filter_keeps_the_people_the_read_flags_by_the_reasons_it_gives(OverviewRuleSet set, string[] kept)
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var ruled = SeedMembership(options, subjectId, "ruled", [Scope.GlucoseRead]);
        var quiet = SeedMembership(options, subjectId, "quiet", [Scope.GlucoseRead]);
        SeedMembership(options, subjectId, "stale", [Scope.GlucoseRead]);
        var rules = new ScriptedRules { Reasons = { [ruled] = [RuleReason(NotificationUrgency.Warn, null)] } };

        var response = await ReadAsync(
            NewService(options, fakes: QuietFakes(ruled, quiet), flagRules: rules), subjectId,
            Filtered(Filter(PeopleFilterKind.Flagged)), set);

        response.Tenants.Select(t => t.Slug).Should().BeEquivalentTo(kept);
    }

    [Fact]
    public async Task Every_filter_must_hold()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var both = SeedMembership(options, subjectId, "both", RecordReader);
        SeedRecord(options, both, today.AddYears(-30));
        var adultOnly = SeedMembership(options, subjectId, "adult-only", RecordReader);
        SeedRecord(options, adultOnly, today.AddYears(-30));
        var factOnly = SeedMembership(options, subjectId, "fact-only", RecordReader);
        SeedRecord(options, factOnly, today.AddYears(-10));
        var rules = new ScriptedRules { FailingFilters = { adultOnly } };

        var response = await ReadAsync(
            NewService(options, fakes: QuietFakes(both, adultOnly, factOnly), flagRules: rules), subjectId,
            Filtered(FactFilter(), Filter(PeopleFilterKind.AgeAtLeast) with { Years = 18 }));

        response.Tenants.Select(t => t.TenantId).Should().Equal(both);
        rules.PeopleBegunFor.Single().Should().BeEquivalentTo([both, adultOnly], "the age filter narrows before any figure is judged");
    }

    [Fact]
    public async Task An_empty_query_reads_as_no_query()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var ruled = SeedMembership(options, subjectId, "ruled", [Scope.GlucoseRead]);
        var calm = SeedMembership(options, subjectId, "calm", [Scope.GlucoseRead]);
        SeedMembership(options, subjectId, "stale", [Scope.GlucoseRead]);
        var rules = new ScriptedRules { Pending = 1, Reasons = { [ruled] = [RuleReason(NotificationUrgency.Warn, null)] } };
        var service = NewService(options, fakes: QuietFakes(ruled, calm), flagRules: rules);

        var none = await ReadAsync(service, subjectId, PeopleQuery.Empty);
        var empty = await ReadAsync(service, subjectId, new PeopleQuery("   ", [], null));
        var fixedSet = await ReadAsync(service, subjectId, new PeopleQuery("", [], null), OverviewRuleSet.Fixed);

        empty.Should().BeEquivalentTo(none, o => o.WithStrictOrdering());
        none.Tenants.Select(t => t.Slug).Should().Equal("ruled", "stale", "calm");
        rules.FactsBegunFor.Should().OnlyContain(facts => facts.Count == 0);
        rules.Begun.Should().Be(2, "a fixed read with an empty query judges nothing");
        fixedSet.PeopleWithRulesPending.Should().Be(0);
    }

    [Fact]
    public async Task A_sort_orders_within_the_flagged_and_within_the_rest()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var high = SeedMembership(options, subjectId, "high", [Scope.GlucoseRead]);
        var middle = SeedMembership(options, subjectId, "middle", [Scope.GlucoseRead]);
        var low = SeedMembership(options, subjectId, "low", [Scope.GlucoseRead]);
        var fakes = new Fakes();
        fakes.Data[high] = new TenantData { Latest = new SensorGlucose { Mgdl = 170, Timestamp = DateTime.UtcNow.AddMinutes(-1) } };
        fakes.Data[middle] = new TenantData { Latest = new SensorGlucose { Mgdl = 110, Timestamp = DateTime.UtcNow.AddMinutes(-1) } };
        fakes.Data[low] = new TenantData { Latest = new SensorGlucose { Mgdl = 50, Timestamp = DateTime.UtcNow.AddMinutes(-1) } };
        var service = NewService(options, fakes: fakes);

        var ascending = await ReadAsync(service, subjectId,
            new PeopleQuery(null, null, new PeopleSort(PeopleSortColumn.Glucose, PeopleSortDirection.Ascending)));
        var descending = await ReadAsync(service, subjectId,
            new PeopleQuery(null, null, new PeopleSort(PeopleSortColumn.Glucose, PeopleSortDirection.Descending)));

        ascending.Tenants.Select(t => t.Slug).Should().Equal("low", "middle", "high");
        descending.Tenants.Select(t => t.Slug).Should().Equal(new[] { "low", "high", "middle" }, "the urgent low stays first");
    }

    private static void SeedRecord(
        DbContextOptions<NocturneDbContext> options, Guid tenantId, DateOnly? dateOfBirth, bool takesNoInsulin = false)
    {
        using var db = new NocturneDbContext(options) { TenantId = tenantId };
        db.PatientRecords.Add(new PatientRecordEntity
        {
            Id = Guid.NewGuid(), TenantId = tenantId, DateOfBirth = dateOfBirth, TakesNoInsulin = takesNoInsulin,
        });
        db.SaveChanges();
    }

    private static void SeedDevice(
        DbContextOptions<NocturneDbContext> options, Guid tenantId, DeviceCategory category, AidAlgorithm algorithm)
    {
        using var db = new NocturneDbContext(options) { TenantId = tenantId };
        db.PatientDevices.Add(new PatientDeviceEntity
        {
            Id = Guid.NewGuid(), TenantId = tenantId, DeviceCategory = category.ToString(), AidAlgorithm = algorithm.ToString(),
            Manufacturer = "Maker", Model = "Model", IsCurrent = true,
        });
        db.SaveChanges();
    }

    private static void SeedInsulin(DbContextOptions<NocturneDbContext> options, Guid tenantId)
    {
        using var db = new NocturneDbContext(options) { TenantId = tenantId };
        db.PatientInsulins.Add(new PatientInsulinEntity
        {
            Id = Guid.NewGuid(), TenantId = tenantId, InsulinCategory = "RapidActing", Name = "Rapid", IsCurrent = true,
        });
        db.SaveChanges();
    }
}
