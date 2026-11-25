using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// <see cref="TeamRenameAnnouncer"/>: one live notice per team and site to the person who approved
/// the team's access there, filed for that site, naming the team by the name they knew and its
/// current name; each change announced as a new notice; removed when the name returns; skipped once
/// they left the site; and never failing the rename.
/// </summary>
[Trait("Category", "Unit")]
public class TeamRenameAnnouncerTests
{
    private static readonly Guid TeamId = Guid.CreateVersion7();
    private static readonly Guid Stayed = Guid.CreateVersion7();
    private static readonly Guid Left = Guid.CreateVersion7();
    private static readonly Guid SiteA = Guid.CreateVersion7();
    private static readonly Guid SiteB = Guid.CreateVersion7();

    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<ITenantMembershipRepository> _members = new();
    private readonly Mock<ITenantRepository> _tenants = new();
    private string _currentName = string.Empty;

    private sealed record Notice(Guid? Tenant, string User, string Type, string Source, string Title, Dictionary<string, object>? Metadata)
    {
        public NotificationArchiveReason? Archived { get; set; }
    }

    private readonly List<Notice> _notices = [];

    private IEnumerable<Notice> Live => _notices.Where(n => n.Archived is null);

    private TeamRenameAnnouncer Announcer(Exception? failure = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor, Accessor>();
        services.AddScoped(provider =>
        {
            Guid? Tenant() => provider.GetRequiredService<ITenantAccessor>().Context?.TenantId;
            var notifications = new Mock<IInAppNotificationService>();
            var read = notifications.Setup(n => n.GetActiveNotificationsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()));
            if (failure is not null)
            {
                read.ThrowsAsync(failure);
                return notifications.Object;
            }

            read.ReturnsAsync((string user, CancellationToken _) => Live
                .Where(n => n.Tenant == Tenant() && n.User == user)
                .Select(n => new InAppNotificationDto { Type = n.Type, SourceId = n.Source, Metadata = n.Metadata })
                .ToList());
            notifications.Setup(n => n.ArchiveBySourceAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<NotificationArchiveReason>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string user, string type, string source, NotificationArchiveReason reason, CancellationToken _) =>
                {
                    var live = Live.FirstOrDefault(n => n.Tenant == Tenant() && n.User == user && n.Type == type && n.Source == source);
                    if (live is null)
                        return false;
                    live.Archived = reason;
                    return true;
                });
            notifications.Setup(n => n.CreateNotificationUnlessActiveAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<NotificationCategory?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string user, string type, string title, string source, NotificationCategory? _, string? _,
                    string? _, Dictionary<string, object>? metadata, CancellationToken _) =>
                {
                    if (Live.Any(n => n.Tenant == Tenant() && n.User == user && n.Type == type && n.Source == source))
                        return false;
                    _notices.Add(new Notice(Tenant(), user, type, source, title, metadata));
                    return true;
                });
            return notifications.Object;
        });

        return new TeamRenameAnnouncer(
            _teams.Object, _members.Object, new SiteNotices(_tenants.Object, services.BuildServiceProvider()),
            TimeProvider.System, NullLogger<TeamRenameAnnouncer>.Instance);
    }

    public TeamRenameAnnouncerTests()
    {
        _teams.Setup(t => t.ListAccessApproversAsync(TeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [new TeamAccessApprover(SiteA, Stayed, _currentName), new TeamAccessApprover(SiteB, Left, _currentName)]);
        _members.Setup(m => m.IsMemberAsync(SiteA, Stayed, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _members.Setup(m => m.IsMemberAsync(SiteB, Left, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        foreach (var site in new[] { SiteA, SiteB })
            _tenants.Setup(t => t.GetAsync(site, It.IsAny<CancellationToken>())).ReturnsAsync(new TenantRecord(
                site, $"s-{site:N}", "Site", true, false, false, false, null, null, null, null, null,
                null, null, null, null, null, null, DateTime.UtcNow, DateTime.UtcNow, null));
    }

    private Task RenameAsync(TeamRenameAnnouncer announcer, string from, string to)
    {
        _currentName = to;
        return announcer.AnnounceAsync(TeamId, from);
    }

    private static Dictionary<string, object> Names(string known, string current) => new()
    {
        [TeamRenameAnnouncer.OldNameKey] = known, [TeamRenameAnnouncer.NewNameKey] = current,
    };

    [Fact]
    public async Task The_approver_still_in_the_site_is_told_in_that_sites_scope_and_one_who_left_is_not()
    {
        await RenameAsync(Announcer(), "Old name", "New name");

        var notice = Live.Should().ContainSingle().Subject;
        (notice.Tenant, notice.User, notice.Type, notice.Source, notice.Title).Should().Be(
            ((Guid?)SiteA, Stayed.ToString(), TeamRenameAnnouncer.NotificationType, TeamId.ToString(), TeamRenameAnnouncer.TitleKey));
        notice.Metadata.Should().Equal(Names("Old name", "New name"));
    }

    [Fact]
    public async Task Renames_while_a_notice_is_live_keep_the_name_the_owner_knew_and_announce_each_change_as_a_new_notice()
    {
        var announcer = Announcer();

        await RenameAsync(announcer, "Camp A", "B");
        await RenameAsync(announcer, "B", "C");
        await RenameAsync(announcer, "C", "D");

        Live.Should().ContainSingle().Which.Metadata.Should().Equal(Names("Camp A", "D"));
        _notices.Should().HaveCount(3, "each change is filed, and so announced, as a new notice");
        _notices.Take(2).Should().OnlyContain(n => n.Archived == NotificationArchiveReason.Superseded);
    }

    [Fact]
    public async Task After_the_owner_dismisses_a_notice_the_next_names_the_team_by_the_name_they_last_saw()
    {
        var announcer = Announcer();
        await RenameAsync(announcer, "Camp A", "B");
        Live.Single().Archived = NotificationArchiveReason.Dismissed;

        await RenameAsync(announcer, "B", "C");

        Live.Should().ContainSingle().Which.Metadata.Should().Equal(Names("B", "C"));
    }

    [Fact]
    public async Task A_rename_back_to_the_name_the_owner_knew_removes_the_notice()
    {
        var announcer = Announcer();
        await RenameAsync(announcer, "Camp A", "B");
        await RenameAsync(announcer, "B", "C");

        await RenameAsync(announcer, "C", "Camp A");

        Live.Should().BeEmpty();
        _notices.Last().Archived.Should().Be(NotificationArchiveReason.ConditionMet);
    }

    [Theory]
    [InlineData(typeof(DbUpdateException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task A_notification_that_cannot_be_filed_does_not_fail_the_rename(Type failure)
    {
        var announce = () => RenameAsync(Announcer((Exception)Activator.CreateInstance(failure)!), "Old", "New");

        await announce.Should().NotThrowAsync();
    }

    private sealed class Accessor : ITenantAccessor
    {
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;

        public bool IsResolved => Context is not null;

        public TenantContext? Context { get; private set; }

        public void SetTenant(TenantContext context) => Context = context;
    }
}
