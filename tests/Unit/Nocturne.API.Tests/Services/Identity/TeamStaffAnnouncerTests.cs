using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Notifications;
using Nocturne.API.Services.NotificationTemplates;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// <see cref="TeamStaffAnnouncer"/>: one quiet notice per owner in force and team at a site, stating how many
/// of the team's staff who do not own the site wait, read after the write; replaced when the count changes,
/// taken back at none and never stacked; a separate notice that the team's staff newly reach the site; no staff
/// names; never reaching a device; each kind with a source of its own; and never failing the write.
/// </summary>
[Trait("Category", "Unit")]
public class TeamStaffAnnouncerTests
{
    private static readonly Guid Site = Guid.CreateVersion7();
    private static readonly Guid OtherSite = Guid.CreateVersion7();
    private static readonly Guid TeamId = Guid.CreateVersion7();
    private static readonly Guid OtherTeam = Guid.CreateVersion7();
    private static readonly Guid OwnerA = Guid.CreateVersion7();
    private static readonly Guid OwnerB = Guid.CreateVersion7();
    private static readonly Guid Viewer = Guid.CreateVersion7();
    private static readonly Guid Manager = Guid.CreateVersion7();

    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<ITenantMembershipRepository> _members = new();
    private readonly Mock<ITenantRepository> _tenants = new();
    private readonly Mock<ILogger<TeamStaffAnnouncer>> _logger = new();
    private readonly RecordingNotices _notices = new();
    private readonly List<Guid> _waiting = [];

    public TeamStaffAnnouncerTests()
    {
        _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        foreach (var site in new[] { Site, OtherSite })
        {
            _members.Setup(m => m.ListLivePeopleAsync(site, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([Member(site, OwnerA, RoleSeeds.Owner), Member(site, OwnerB, RoleSeeds.Owner),
                    Member(site, Viewer, RoleSeeds.Viewer), Member(site, Manager, RoleSeeds.Caretaker)]);
            _tenants.Setup(t => t.GetAsync(site, It.IsAny<CancellationToken>())).ReturnsAsync(new TenantRecord(
                site, $"s-{site:N}", "Site", true, false, false, false, null, null, null, null, null,
                null, null, null, null, null, null, DateTime.UtcNow, DateTime.UtcNow, null));
            Reads(site, TeamId, () => new TeamStaffWaiting("Ward 4", _waiting.ToList()));
        }
    }

    private void Reads(Guid site, Guid team, Func<TeamStaffWaiting?> read) =>
        _teams.Setup(t => t.WithStaffWaitingLockedAsync(
                site, team, It.IsAny<DateTime>(), It.IsAny<Func<TeamStaffWaiting?, Task>>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Guid _, DateTime _, Func<TeamStaffWaiting?, Task> work, CancellationToken _) => work(read()));

    private static TenantMemberRecord Member(Guid site, Guid subject, string role) => new(
        Guid.CreateVersion7(), site, subject, "Person", null, true, false, false, false, false,
        [new TenantMemberRoleRecord(Guid.CreateVersion7(), role, role, [Scope.GlucoseRead])],
        null, null, null, false, null, null, null, DateTime.UtcNow, []);

    private TeamStaffAnnouncer Announcer() => new(
        _teams.Object, _members.Object, new SiteNotices(_tenants.Object, _notices.Services()),
        TimeProvider.System, _logger.Object);

    private void Waiting(int count)
    {
        _waiting.Clear();
        _waiting.AddRange(Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()));
    }

    private static StaffNotices Recount(Guid site, Guid? team = null) =>
        new([new TeamSiteKey(site, team ?? TeamId)], []);

    private static StaffNotices Joined(Guid site) => new([], [new TeamSiteKey(site, TeamId)]);

    private IEnumerable<RecordingNotices.Notice> Live(string type) => _notices.Live.Where(n => n.Type == type);

    private static int CountOf(RecordingNotices.Notice notice) => int.Parse(notice.Metadata![TeamStaffAnnouncer.CountKey].ToString()!);

    [Fact]
    public async Task Each_owner_in_force_gets_one_notice_in_that_sites_scope_and_no_one_else_does()
    {
        Waiting(3);

        await Announcer().AnnounceAsync(Recount(Site));

        Live(TeamStaffAnnouncer.WaitingType).Select(n => (n.Tenant, n.User, n.Source, n.Title)).Should().BeEquivalentTo(new (Guid?, string, string, string)[]
        {
            (Site, OwnerA.ToString(), TeamId.ToString(), TeamStaffAnnouncer.WaitingTitleKey),
            (Site, OwnerB.ToString(), TeamId.ToString(), TeamStaffAnnouncer.WaitingTitleKey),
        });
    }

    [Fact]
    public async Task The_notice_states_the_count_and_names_only_the_team()
    {
        Waiting(11);

        await Announcer().AnnounceAsync(Recount(Site));

        Live(TeamStaffAnnouncer.WaitingType).First().Metadata.Should().Equal(new Dictionary<string, object>
        {
            [TeamStaffAnnouncer.TeamKey] = "Ward 4",
            [TeamStaffAnnouncer.TeamIdKey] = TeamId,
            [TeamStaffAnnouncer.CountKey] = 11,
        });
    }

    [Fact]
    public async Task A_staff_member_who_owns_the_site_is_not_counted()
    {
        Waiting(2);
        _waiting.Add(OwnerA);

        await Announcer().AnnounceAsync(Recount(Site));

        Live(TeamStaffAnnouncer.WaitingType).Should().OnlyContain(n => CountOf(n) == 2);
    }

    [Fact]
    public async Task A_count_that_changes_replaces_the_notice_and_one_that_does_not_files_nothing()
    {
        var announcer = Announcer();
        Waiting(5);
        await announcer.AnnounceAsync(Recount(Site));
        var filed = _notices.All.Count;

        await announcer.AnnounceAsync(Recount(Site));
        _notices.All.Should().HaveCount(filed, "the count is the same");

        Waiting(3);
        await announcer.AnnounceAsync(Recount(Site));

        Live(TeamStaffAnnouncer.WaitingType).Should().HaveCount(2).And.OnlyContain(n => CountOf(n) == 3);
        _notices.All.Where(n => n.Archived is not null).Should().OnlyContain(n => n.Archived == NotificationArchiveReason.Superseded);
    }

    [Fact]
    public async Task No_one_waiting_takes_the_notice_back_and_files_none()
    {
        var announcer = Announcer();
        await announcer.AnnounceAsync(Recount(Site));
        _notices.All.Should().BeEmpty();
        Waiting(2);
        await announcer.AnnounceAsync(Recount(Site));

        Waiting(0);
        await announcer.AnnounceAsync(Recount(Site));

        _notices.Live.Should().BeEmpty();
        _notices.All.Should().OnlyContain(n => n.Archived == NotificationArchiveReason.ConditionMet);
    }

    [Fact]
    public async Task A_team_with_no_access_to_the_site_takes_the_notice_back()
    {
        var announcer = Announcer();
        Waiting(2);
        await announcer.AnnounceAsync(Recount(Site));
        Reads(Site, TeamId, () => null);

        await announcer.AnnounceAsync(Recount(Site));

        _notices.Live.Should().BeEmpty();
    }

    [Fact]
    public async Task A_recount_leaves_the_notice_of_another_team_and_another_site()
    {
        var announcer = Announcer();
        Waiting(2);
        Reads(Site, OtherTeam, () => new TeamStaffWaiting("Ward 5", [Guid.CreateVersion7()]));
        await announcer.AnnounceAsync(new StaffNotices(
            [new TeamSiteKey(Site, TeamId), new TeamSiteKey(Site, OtherTeam), new TeamSiteKey(OtherSite, TeamId)], []));

        Waiting(0);
        await announcer.AnnounceAsync(Recount(Site));

        Live(TeamStaffAnnouncer.WaitingType).Select(n => (n.Tenant, n.Source)).Should().BeEquivalentTo(new (Guid?, string)[]
        {
            (Site, OtherTeam.ToString()), (Site, OtherTeam.ToString()), (OtherSite, TeamId.ToString()), (OtherSite, TeamId.ToString()),
        });
    }

    [Fact]
    public async Task Staff_who_newly_reach_the_site_are_announced_once_a_team_and_a_recount_leaves_that_notice()
    {
        var announcer = Announcer();

        await announcer.AnnounceAsync(Joined(Site));
        await announcer.AnnounceAsync(Joined(Site));
        Waiting(0);
        await announcer.AnnounceAsync(Recount(Site));

        var joined = Live(TeamStaffAnnouncer.JoinedType).ToList();
        joined.Select(n => (n.User, n.Source, n.Title)).Should().BeEquivalentTo(new[]
        {
            (OwnerA.ToString(), TeamId.ToString(), TeamStaffAnnouncer.JoinedTitleKey),
            (OwnerB.ToString(), TeamId.ToString(), TeamStaffAnnouncer.JoinedTitleKey),
        });
        joined.Should().OnlyContain(n => n.Metadata!.Count == 2 && (string)n.Metadata[TeamStaffAnnouncer.TeamKey] == "Ward 4");
        _notices.All.Should().HaveCount(2, "the second announcement files nothing while one is active");
    }

    [Fact]
    public async Task A_team_that_is_gone_files_no_joined_notice()
    {
        Reads(Site, TeamId, () => null);

        await Announcer().AnnounceAsync(Joined(Site));

        _notices.Live.Should().BeEmpty();
    }

    [Fact]
    public async Task Waiting_and_joined_notices_of_a_team_are_live_together()
    {
        Waiting(1);

        await Announcer().AnnounceAsync(new StaffNotices([new TeamSiteKey(Site, TeamId)], [new TeamSiteKey(Site, TeamId)]));

        _notices.Live.Select(n => n.Type).Distinct().Should().BeEquivalentTo(TeamStaffAnnouncer.WaitingType, TeamStaffAnnouncer.JoinedType);
    }

    [Fact]
    public async Task An_owner_change_recounts_every_team_with_access_to_the_site()
    {
        Waiting(2);
        Reads(Site, OtherTeam, () => new TeamStaffWaiting("Ward 5", [Guid.CreateVersion7()]));
        _teams.Setup(t => t.ListAccessOfTenantAsync(Site, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Access(TeamId), Access(OtherTeam)]);

        await Announcer().RecountOwnerChangeAsync(Site, Guid.CreateVersion7());

        Live(TeamStaffAnnouncer.WaitingType).Select(n => (n.Source, CountOf(n))).Should().BeEquivalentTo(new[]
        {
            (TeamId.ToString(), 2), (TeamId.ToString(), 2), (OtherTeam.ToString(), 1), (OtherTeam.ToString(), 1),
        });
    }

    private static TenantTeamAccess Access(Guid team) =>
        new(team, "Ward", [], false, null, false, null, null, DateTime.UtcNow, false, []);

    [Fact]
    public void The_waiting_the_joined_and_the_rename_notice_each_have_a_source_of_their_own()
    {
        var templates = new NotificationTemplateRegistry().AddBuiltInTemplates();

        var sources = new[] { TeamStaffAnnouncer.WaitingType, TeamStaffAnnouncer.JoinedType, TeamRenameAnnouncer.NotificationType }
            .Select(type => templates.GetTemplate(type)!.Source)
            .ToList();

        sources.Should().Equal(TeamStaffAnnouncer.WaitingSource, TeamStaffAnnouncer.JoinedSource, TeamRenameAnnouncer.Source)
            .And.OnlyHaveUniqueItems("a notice of one kind must never push out a notice of another");
    }

    [Theory]
    [InlineData(TeamStaffAnnouncer.WaitingType, TeamStaffAnnouncer.WaitingTitleKey)]
    [InlineData(TeamStaffAnnouncer.JoinedType, TeamStaffAnnouncer.JoinedTitleKey)]
    public void A_notice_never_reaches_a_device(string type, string title)
    {
        DeviceNotificationMirror.Qualifies(new InAppNotificationDto
        {
            Type = type,
            Title = title,
            Category = NotificationCategory.Informational,
            Urgency = NotificationUrgency.Info,
        }).Should().BeFalse("a label key reads as nothing on a device, which has no translations");
        DeviceNotificationMirror.IsLabelKey(title).Should().BeTrue();
        new NotificationTemplateRegistry().AddBuiltInTemplates().GetTemplate(type)!.Category.Should().Be(NotificationCategory.Informational);
    }

    [Theory]
    [InlineData(typeof(DbUpdateException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task A_store_that_fails_is_logged_and_does_not_fail_the_write(Type failure)
    {
        _notices.Failure = (Exception)Activator.CreateInstance(failure)!;
        Waiting(2);

        var announce = () => Announcer().AnnounceAsync(Recount(Site));

        await announce.Should().NotThrowAsync();
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning, It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(), It.Is<Exception>(e => e.GetType() == failure),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task A_site_that_fails_does_not_stop_the_next_site()
    {
        Waiting(1);
        _tenants.Setup(t => t.GetAsync(Site, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());

        await Announcer().AnnounceAsync(new StaffNotices([new TeamSiteKey(Site, TeamId), new TeamSiteKey(OtherSite, TeamId)], []));

        _notices.Live.Should().OnlyContain(n => n.Tenant == OtherSite).And.NotBeEmpty();
    }
}
