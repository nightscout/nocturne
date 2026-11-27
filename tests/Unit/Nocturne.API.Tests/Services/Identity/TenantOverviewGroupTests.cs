using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using static Nocturne.API.Tests.Services.Identity.TenantOverviewTestKit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// One group's overview (<see cref="TenantOverviewService.GetGroupOverviewAsync"/>): the people of the group the
/// caller reads, as the overview of everyone orders and flags them, and how many others the group holds, by number only.
/// </summary>
[Trait("Category", "Unit")]
public class TenantOverviewGroupTests
{
    private static readonly IReadOnlySet<string> FullTokenScopes = new HashSet<string> { Scope.FullAccess };
    private static readonly List<string> GlucoseReader = [Scope.GlucoseRead];

    private readonly Guid _subject = Guid.NewGuid();
    private readonly Guid _group = Guid.NewGuid();
    private readonly DbContextOptions<NocturneDbContext> _options = NewOptions();
    private readonly Mock<ITeamRepository> _teams = InNoGroup();
    private readonly Dictionary<Guid, SensorGlucose> _latest = [];
    private readonly ErrorLog _errors = new();

    [Fact]
    public async Task A_group_lists_the_people_the_caller_reads_in_the_overviews_order_and_counts_the_others()
    {
        var zed = SeedMembership(_options, _subject, "zed", GlucoseReader);
        var bo = Reading(SeedMembership(_options, _subject, "bo", GlucoseReader));
        var cy = Reading(SeedMembership(_options, _subject, "cy", GlucoseReader));
        Reading(SeedMembership(_options, _subject, "dee", GlucoseReader));
        var noGlucose = SeedMembership(_options, _subject, "no-glucose", [Scope.TreatmentsRead]);
        var inactive = SeedMembership(_options, _subject, "inactive", GlucoseReader, tenantActive: false);
        var stranger = SeedMembership(_options, Guid.NewGuid(), "stranger", GlucoseReader);
        InGroup(cy, zed, bo, noGlucose, inactive, stranger);

        var group = await Service().GetGroupOverviewAsync(
            _subject, _group, FullTokenScopes, AuthType.SessionCookie, false, CancellationToken.None);

        group.Should().NotBeNull();
        _errors.Messages.Should().BeEmpty();
        group!.Tenants.Select(t => t.Slug).Should().Equal("zed", "bo", "cy");
        group.Tenants[0].Attention.Should().NotBeEmpty("zed has no reading, so the flag puts them first");
        group.Tenants.Skip(1).Should().OnlyContain(t => t.Attention.Count == 0);
        group.HiddenCount.Should().Be(3);

        var everyone = await Service().GetOverviewAsync(_subject, FullTokenScopes, AuthType.SessionCookie);
        everyone.Tenants.Select(t => t.Slug).Should().Equal("zed", "bo", "cy", "dee");
        everyone.HiddenCount.Should().BeNull();
    }

    [Fact]
    public async Task No_response_names_a_person_of_the_group_the_caller_cannot_read()
    {
        var read = Reading(SeedMembership(_options, _subject, "read", GlucoseReader));
        var narrowed = SeedMembership(_options, _subject, "narrowed-pat", [Scope.TreatmentsRead]);
        var stranger = SeedMembership(_options, Guid.NewGuid(), "stranger-sam", GlucoseReader);
        InGroup(read, narrowed, stranger);

        var group = await Service().GetGroupOverviewAsync(
            _subject, _group, FullTokenScopes, AuthType.SessionCookie, false, CancellationToken.None);
        var review = await Service().GetGroupReviewAsync(
            _subject, _group, FullTokenScopes, AuthType.SessionCookie, 14, false, CancellationToken.None);

        group!.HiddenCount.Should().Be(2);
        foreach (var json in new[] { JsonSerializer.Serialize(group), JsonSerializer.Serialize(review) })
        {
            json.Should().Contain(read.ToString());
            foreach (var hidden in new[] { narrowed.ToString(), stranger.ToString(), "narrowed-pat", "stranger-sam" })
                json.Should().NotContain(hidden);
        }
    }

    [Fact]
    public async Task A_group_the_caller_is_not_in_has_no_overview_and_no_review()
    {
        SeedMembership(_options, _subject, "amy", GlucoseReader);
        _teams.Setup(t => t.GetPeopleOfStaffGroupAsync(_group, _subject, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<Guid>?)null);

        (await Service().GetGroupOverviewAsync(
            _subject, _group, FullTokenScopes, AuthType.SessionCookie, false, CancellationToken.None)).Should().BeNull();
        (await Service().GetGroupReviewAsync(
            _subject, _group, FullTokenScopes, AuthType.SessionCookie, 14, false, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_group_where_the_caller_reads_nobody_lists_nobody_and_counts_everyone_in_it()
    {
        SeedMembership(_options, _subject, "elsewhere", GlucoseReader);
        InGroup(Guid.NewGuid(), Guid.NewGuid());

        var group = await Service().GetGroupOverviewAsync(
            _subject, _group, FullTokenScopes, AuthType.SessionCookie, false, CancellationToken.None);

        group!.Tenants.Should().BeEmpty();
        group.HiddenCount.Should().Be(2);
    }

    [Fact]
    public async Task Everyone_puts_each_person_under_the_group_the_teams_read_names_and_a_group_puts_nobody_under_one()
    {
        var amy = SeedMembership(_options, _subject, "amy", GlucoseReader);
        var bo = SeedMembership(_options, _subject, "bo", GlucoseReader);
        var birch = Guid.NewGuid();
        _teams.Setup(t => t.GetFirstGroupOfPeopleAsync(_subject, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, Guid> { [amy] = birch, [Guid.NewGuid()] = Guid.NewGuid() });
        InGroup(amy, bo);

        var everyone = await Service().GetOverviewAsync(_subject, FullTokenScopes, AuthType.SessionCookie);
        everyone.Tenants.ToDictionary(t => t.Slug, t => t.FirstGroupId).Should().BeEquivalentTo(
            new Dictionary<string, Guid?> { ["amy"] = birch, ["bo"] = null });

        var group = await Service().GetGroupOverviewAsync(
            _subject, _group, FullTokenScopes, AuthType.SessionCookie, false, CancellationToken.None);
        group!.Tenants.Should().HaveCount(2).And.OnlyContain(t => t.FirstGroupId == null);
    }

    private void InGroup(params Guid[] tenants) =>
        _teams.Setup(t => t.GetPeopleOfStaffGroupAsync(_group, _subject, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenants.ToHashSet());

    private Guid Reading(Guid tenant)
    {
        _latest[tenant] = new SensorGlucose { Mgdl = 120, Timestamp = DateTime.UtcNow.AddMinutes(-2) };
        return tenant;
    }

    private TenantOverviewService Service()
    {
        var current = Guid.Empty;
        var accessor = new Mock<ITenantAccessor>();
        accessor.Setup(a => a.SetTenant(It.IsAny<TenantContext>())).Callback<TenantContext>(tc => current = tc.TenantId);
        var canonical = new Mock<ICanonicalGlucoseService>();
        canonical.Setup(s => s.GetLatestAsync(It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(_latest.GetValueOrDefault(current)));
        canonical.Setup(s => s.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var services = new ServiceCollection();
        services.AddScoped(_ => accessor.Object);
        services.AddScoped<ICategoryReadContext>(_ => new CategoryReadContext());
        services.AddScoped<INocturneDbContextFactory>(_ => new ScopeContextFactory(_options, () => current));
        services.AddScoped<IAlertRepository>(sp => new AlertRepository(sp.GetRequiredService<INocturneDbContextFactory>()));
        services.AddScoped(_ => canonical.Object);

        return new TenantOverviewService(
            new TenantMembershipRepository(new InMemoryContextFactory(_options).AsNocturneFactory(), AccountLinkRoutes.Off),
            _teams.Object,
            services.BuildServiceProvider().CreateScope().ServiceProvider,
            new GlucoseStatusClassifier(new ConfigurationBuilder().Build(), NullLogger<GlucoseStatusClassifier>.Instance),
            TimeProvider.System,
            _errors);
    }
}
