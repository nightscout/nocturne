using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// <see cref="TeamAccessService"/>: only an owner of the site approves, the approver's own access
/// is read from their membership of the chosen site, and the limit and end are never wider than the
/// link or the approver. The roles' check under the lock is <c>TeamAccessPostgresTests</c>'.
/// </summary>
[Trait("Category", "Unit")]
public class TeamAccessServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Caller = Guid.CreateVersion7();
    private static readonly Guid TeamId = Guid.CreateVersion7();
    private static readonly Guid SiteId = Guid.CreateVersion7();
    private static readonly Guid OwnerRoleId = Guid.CreateVersion7();
    private const string Token = "token-hash";

    private readonly Mock<ITeamRepository> _teams = new(MockBehavior.Strict);
    private readonly Mock<ITenantMembershipRepository> _members = new();
    private readonly Mock<ITenantRoleService> _roles = new();

    private TeamAccessService Service() => new(
        _teams.Object, _members.Object, _roles.Object, new FakeTimeProvider(new DateTimeOffset(Now)),
        new Mock<INocturneDbContextFactory>(MockBehavior.Strict).Object,
        new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
        new TeamStaffAnnouncer(
            _teams.Object, _members.Object, new SiteNotices(Mock.Of<ITenantRepository>(), new ServiceCollection().BuildServiceProvider()),
            TimeProvider.System, NullLogger<TeamStaffAnnouncer>.Instance));

    /// <summary>
    /// Whether the approval passes every check made before the write: the write opens a unit on the
    /// strict context factory, which no test sets up.
    /// </summary>
    private async Task PassesTheChecksAsync(TeamAccessApproverCredential caller, TeamAccessGrant grant)
    {
        var approve = () => Service().ApproveAsync(Token, caller, grant, default);
        await approve.Should().ThrowAsync<MockException>();
    }

    private static TeamAccessApproverCredential Session(bool limited = false) =>
        new(Caller, AuthType.SessionCookie, new HashSet<string>(), limited);

    private static TeamAccessSuggestion Suggestion(bool limit = false, DateTime? end = null) =>
        new(RoleSeeds.Caretaker, limit, end);

    private void Link(TeamAccessSuggestion? suggestion = null, TeamAccessRefusal? refusal = null) =>
        _teams.Setup(t => t.GetAccessLinkInfoAsync(Token, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TeamAccessLinkInfo(Guid.CreateVersion7(), TeamId, "Clinic", "Sam", suggestion ?? Suggestion(), Now.AddDays(1), refusal));

    private static SubjectMembership Membership(
        Guid tenantId, string roleSlug = RoleSeeds.Owner, bool active = true, bool limited = false, DateTime? end = null,
        string name = "Site") =>
        new(
            new TenantRecord(tenantId, $"s-{tenantId:N}", name, active, false, false, false, null, null, null, null, null,
                null, null, null, null, null, null, Now, Now, null),
            new TenantMemberRecord(
                Guid.CreateVersion7(), tenantId, Caller, "Sam", null, true, false, false, false, false,
                [new TenantMemberRoleRecord(OwnerRoleId, roleSlug, roleSlug, RoleSeeds.Permissions[roleSlug])],
                null, null, null, limited, end, null, null, Now, []));

    private void Memberships(params SubjectMembership[] memberships) =>
        _members.Setup(m => m.ListMembershipsOfSubjectAsync(Caller, Now, It.IsAny<CancellationToken>())).ReturnsAsync(memberships);

    private void GrantValid() =>
        _roles.Setup(r => r.ValidateRoleGrantAsync(SiteId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoleGrantValidation.Valid);

    private static TeamAccessGrant Grant(bool limit = false, DateTime? end = null) =>
        new(SiteId, [Guid.CreateVersion7()], limit, end, true);

    public static TheoryData<TeamAccessSuggestion, bool, DateTime?, TeamAccessGrant, TeamAccessRefusal?> Narrowings => new()
    {
        { Suggestion(limit: true), false, null, Grant(limit: true), null },
        { Suggestion(limit: true), false, null, Grant(limit: false), TeamAccessRefusal.LimitRequired },
        { Suggestion(), false, null, Grant(limit: false), null },
        { Suggestion(end: Now.AddDays(7)), false, null, Grant(end: Now.AddDays(7)), null },
        { Suggestion(end: Now.AddDays(7)), false, null, Grant(end: Now.AddDays(7).AddSeconds(1)), TeamAccessRefusal.EndLaterThanLink },
        { Suggestion(end: Now.AddDays(7)), false, null, Grant(end: null), TeamAccessRefusal.EndLaterThanLink },
        { Suggestion(), false, null, Grant(end: Now), TeamAccessRefusal.EndInPast },
        { Suggestion(), false, null, Grant(end: Now.AddSeconds(1)), null },
        { Suggestion(), true, null, Grant(limit: false), TeamAccessRefusal.LimitRequired },
        { Suggestion(), true, null, Grant(limit: true), null },
        { Suggestion(), false, Now.AddDays(3), Grant(end: Now.AddDays(3)), null },
        { Suggestion(), false, Now.AddDays(3), Grant(end: Now.AddDays(3).AddSeconds(1)), TeamAccessRefusal.EndOutlastsApprover },
        { Suggestion(), false, Now.AddDays(3), Grant(end: null), TeamAccessRefusal.EndOutlastsApprover },
    };

    [Theory]
    [MemberData(nameof(Narrowings))]
    public void The_limit_and_end_are_never_wider_than_the_link_or_the_approver(
        TeamAccessSuggestion suggestion, bool approverLimited, DateTime? approverEnd, TeamAccessGrant grant, TeamAccessRefusal? refusal) =>
        TeamAccessRules.Refusal(suggestion, approverLimited, approverEnd, grant, Now).Should().Be(refusal);

    [Fact]
    public async Task A_person_who_does_not_own_the_site_is_refused_and_nothing_is_written()
    {
        Link();
        Memberships(
            Membership(SiteId, RoleSeeds.Admin),
            Membership(Guid.CreateVersion7()));

        (await Service().ApproveAsync(Token, Session(), Grant(), default)).Refusal.Should().Be(TeamAccessRefusal.NotOwner);
    }

    [Fact]
    public async Task An_owner_of_an_inactive_site_is_refused()
    {
        Link();
        Memberships(Membership(SiteId, active: false));

        (await Service().ApproveAsync(Token, Session(), Grant(), default)).Refusal.Should().Be(TeamAccessRefusal.NotOwner);
    }

    [Fact]
    public async Task An_owner_whose_own_access_ends_cannot_give_access_that_outlasts_it()
    {
        var end = Now.AddDays(2);
        Link();
        Memberships(Membership(SiteId, end: end));
        GrantValid();

        (await Service().ApproveAsync(Token, Session(), Grant(), default)).Refusal.Should().Be(TeamAccessRefusal.EndOutlastsApprover);
        (await Service().ApproveAsync(Token, Session(), Grant(end: end.AddMinutes(1)), default)).Refusal
            .Should().Be(TeamAccessRefusal.EndOutlastsApprover);
        await PassesTheChecksAsync(Session(), Grant(end: end));
    }

    [Fact]
    public async Task An_approver_limited_to_24_hours_must_give_the_limit()
    {
        Link();
        Memberships(Membership(SiteId));
        GrantValid();

        (await Service().ApproveAsync(Token, Session(limited: true), Grant(), default)).Refusal.Should().Be(TeamAccessRefusal.LimitRequired);
        await PassesTheChecksAsync(Session(limited: true), Grant(limit: true));
    }

    [Fact]
    public async Task An_owners_membership_flag_does_not_limit_them_since_an_owner_is_exempt_from_the_clamp()
    {
        Link();
        Memberships(Membership(SiteId, limited: true));
        GrantValid();

        await PassesTheChecksAsync(Session(), Grant());
    }

    [Theory]
    [InlineData(RoleGrantValidation.ForeignRole, TeamAccessRefusal.RoleNotInTenant)]
    [InlineData(RoleGrantValidation.ExceedsGranter, TeamAccessRefusal.RoleWiderThanApprover)]
    public async Task The_roles_are_checked_against_what_the_approver_holds_on_the_site(string code, TeamAccessRefusal refusal)
    {
        Link();
        Memberships(Membership(SiteId));
        _roles.Setup(r => r.ValidateRoleGrantAsync(
                SiteId, It.IsAny<IReadOnlyCollection<Guid>>(),
                It.Is<IReadOnlyCollection<string>>(s => s.Contains(Scope.FullAccess)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoleGrantValidation(false, code, "no"));

        (await Service().ApproveAsync(Token, Session(), Grant(), default)).Refusal.Should().Be(refusal);
    }

    [Fact]
    public async Task An_unusable_link_answers_with_its_reason_alone()
    {
        Link(refusal: TeamAccessRefusal.Expired);

        var (details, refusal) = await Service().GetLinkAsync(Token, Session(), default);

        details.Should().BeNull();
        refusal.Should().Be(TeamAccessRefusal.Expired);
        _members.Verify(m => m.ListMembershipsOfSubjectAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_link_lists_only_the_callers_own_sites_with_the_roles_they_may_choose()
    {
        var other = Guid.CreateVersion7();
        var notOwned = Guid.CreateVersion7();
        var linkEnd = Now.AddDays(30);
        var ownEnd = Now.AddDays(10);
        Link(Suggestion(limit: false, end: linkEnd));
        Memberships(
            Membership(SiteId, name: "B site", end: ownEnd),
            Membership(other, name: "A site"),
            Membership(notOwned, RoleSeeds.Caretaker, name: "Not mine"));
        _teams.Setup(t => t.ListSitesWithAccessAsync(TeamId, It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { other });
        var caretaker = Role("Caretaker", RoleSeeds.Caretaker, RoleSeeds.Permissions[RoleSeeds.Caretaker]);
        var narrow = Role("Nights", "nights", [Scope.GlucoseRead]);
        _roles.Setup(r => r.GetRolesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                caretaker,
                narrow,
                Role("Administrator", RoleSeeds.Admin, RoleSeeds.Permissions[RoleSeeds.Admin]),
                Role("Owner", RoleSeeds.Owner, RoleSeeds.Permissions[RoleSeeds.Owner]),
                Role("Wider", "wider", [Scope.GlucoseReadWrite]),
                Role("Not grantable", "not-grantable", [Scope.GlucoseRead]) with { CanGrant = false },
                Role("Denied", RoleSeeds.Denied, []),
            ]);

        var (details, _) = await Service().GetLinkAsync(Token, Session(), default);

        details!.TeamName.Should().Be("Clinic");
        details.SuggestedRolePermissions.Should().Equal(RoleSeeds.Permissions[RoleSeeds.Caretaker]);
        details.Sites.Select(s => s.DisplayName).Should().Equal("A site", "B site");
        details.Sites[0].AlreadyGiven.Should().BeTrue();
        var mine = details.Sites[1];
        mine.TenantId.Should().Be(SiteId);
        mine.AlreadyGiven.Should().BeFalse();
        mine.Roles.Select(r => r.Slug).Should().Equal(RoleSeeds.Caretaker, "nights");
        mine.DefaultRoleId.Should().Be(caretaker.Id);
        mine.LimitForced.Should().BeFalse();
        mine.LatestEnd.Should().Be(ownEnd);
        details.Sites[0].LatestEnd.Should().Be(linkEnd);
    }

    private static TenantRoleDto Role(string name, string slug, List<string> permissions) =>
        new(Guid.CreateVersion7(), name, slug, null, permissions, false, 0, Now, CanGrant: true);
}
