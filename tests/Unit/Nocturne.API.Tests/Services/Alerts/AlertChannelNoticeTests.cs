using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The owners' notifications <see cref="AlertChannelNotice"/> keeps: one per channel of an enabled
/// rule that does not send, raised once, and archived when the channel sends again or leaves its rule.
/// </summary>
[Trait("Category", "Unit")]
public class AlertChannelNoticeTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Sitter = Guid.CreateVersion7();
    private static readonly string[] Owners = [Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString()];

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly Mock<IAlertRuleRepository> _rules = new();
    private readonly Mock<ITenantMembershipRepository> _members = new();
    private readonly Dictionary<string, List<InAppNotificationDto>> _active = Owners.ToDictionary(o => o, _ => new List<InAppNotificationDto>());
    private readonly List<(string Owner, string SourceId, string Title)> _created = [];
    private readonly Dictionary<string, Dictionary<string, object>?> _metadata = [];
    private readonly HashSet<Guid> _deleted = [];
    private readonly List<(string Owner, string SourceId)> _archived = [];
    private readonly AlertChannelNoticeLog _log;
    private readonly AlertRuleSnapshot _rule;
    private readonly AlertRuleChannelSnapshot _inAppToSitter;
    private readonly AlertRuleChannelSnapshot _stopped;
    private readonly AlertRuleChannelSnapshot _sending;
    private List<AlertRuleChannelSnapshot> _channels;
    private bool _sitterIsLive;

    public AlertChannelNoticeTests()
    {
        _log = new AlertChannelNoticeLog(_clock);
        _rule = new AlertRuleSnapshot(
            Guid.CreateVersion7(), Tenant, "Urgent low", AlertConditionType.Threshold, "{}", AlertRuleSeverity.Critical,
            "{}", 0, false, null, ScopeClass: RuleScopeClass.Low);
        _inAppToSitter = new(Guid.CreateVersion7(), _rule.Id, ChannelType.InApp, Sitter.ToString(), null, 0);
        _stopped = new(Guid.CreateVersion7(), _rule.Id, ChannelType.Webhook, "https://example.test/hook", "Family", 1,
            AddedBySubjectId: Sitter, StopsAt: Start.UtcDateTime.AddHours(-1));
        _sending = new(Guid.CreateVersion7(), _rule.Id, ChannelType.Webhook, "https://example.test/other", null, 2);
        _channels = [_inAppToSitter, _stopped, _sending];

        _rules.Setup(r => r.GetChannelsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Dictionary<Guid, IReadOnlyList<AlertRuleChannelSnapshot>> { [_rule.Id] = _channels });
        _members.Setup(m => m.ListLivePeopleAsync(Tenant, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _sitterIsLive ? [SitterMembership()] : []);
    }

    private static TenantMemberRecord SitterMembership() => new(
        Id: Guid.CreateVersion7(), TenantId: Tenant, SubjectId: Sitter, SubjectName: "Sitter",
        SubjectUsername: null, SubjectIsActive: true, IsSystemSubject: false, IsDemoSubject: false,
        IsPlatformAdmin: false, HasConfirmedEmail: true, Roles: [], DirectPermissions: ["*"], Label: null,
        Username: null, LimitTo24Hours: false, AccessEndsAt: null, LastUsedAt: null, CreatedFromInviteId: null,
        SysCreatedAt: Start.UtcDateTime, Reasons: []);

    private AlertChannelNotice Notice()
    {
        var notifications = new Mock<IInAppNotificationService>();
        notifications.Setup(n => n.GetActiveNotificationsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string owner, CancellationToken _) => _active[owner].ToList());
        notifications.Setup(n => n.CreateNotificationUnlessActiveAsync(
                It.IsAny<string>(), NotificationSubjectNaming.AlertChannelNotSending, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string owner, string type, string title, string sourceId, NotificationCategory? _, string? _,
                string? _, Dictionary<string, object>? metadata, CancellationToken _) =>
            {
                _created.Add((owner, sourceId, title));
                _metadata[sourceId] = metadata;
                _active[owner].Add(new InAppNotificationDto { Type = type, SourceId = sourceId, Title = title });
                return true;
            });
        notifications.Setup(n => n.ArchiveBySourceAsync(
                It.IsAny<string>(), NotificationSubjectNaming.AlertChannelNotSending, It.IsAny<string>(),
                NotificationArchiveReason.ConditionMet, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string owner, string _, string sourceId, NotificationArchiveReason _, CancellationToken _) =>
            {
                _archived.Add((owner, sourceId));
                _active[owner].RemoveAll(n => n.SourceId == sourceId);
                return true;
            });

        var owners = new Mock<ITenantOwnerResolver>();
        owners.Setup(o => o.GetOwnerSubjectIdsAsync(Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(Owners);

        var subjects = new Mock<ISubjectRepository>();
        subjects.Setup(r => r.GetManyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                ids.Where(id => !_deleted.Contains(id)).Select(Subject).ToList());

        return new AlertChannelNotice(
            _rules.Object,
            _members.Object,
            subjects.Object,
            new TestNocturneDbContextFactory(TestDbContextFactory.CreateInMemoryContext($"channel_notice_{Guid.NewGuid()}")),
            owners.Object,
            notifications.Object,
            MockTenantAccessor.Create(Tenant).Object,
            _log,
            NullLogger<AlertChannelNotice>.Instance);
    }

    private static SubjectRecord Subject(Guid id) => new(
        id, "Sitter", null, null, null, null, IsActive: true, IsSystemSubject: false, IsDemoSubject: false,
        IsPlatformAdmin: false, "Approved", null, null, null, null, Start.UtcDateTime, Start.UtcDateTime, null, []);

    private Task CheckAsync() => Notice().CheckAsync([_rule], _clock.GetUtcNow().UtcDateTime, CancellationToken.None);

    /// <summary>Lets the next run read the owners' notifications again rather than trust the last reconcile.</summary>
    private void PastFreshness() => _clock.Advance(AlertChannelNoticeLog.Freshness);

    [Fact]
    public async Task Each_owner_is_told_once_about_each_channel_that_does_not_send()
    {
        await CheckAsync();
        PastFreshness();
        await CheckAsync();

        _created.Should().BeEquivalentTo(Owners.SelectMany(o => new[]
        {
            (o, _inAppToSitter.Id.ToString(), "alert_channel_no_recipient"),
            (o, _stopped.Id.ToString(), "alert_channel_stopped"),
        }));
        _archived.Should().BeEmpty();
    }

    [Fact]
    public async Task The_notification_is_archived_when_the_channel_sends_again()
    {
        await CheckAsync();

        _sitterIsLive = true;
        PastFreshness();
        await CheckAsync();

        _archived.Should().BeEquivalentTo(Owners.Select(o => (o, _inAppToSitter.Id.ToString())));
        _active.Values.SelectMany(n => n).Select(n => n.SourceId).Should().AllBe(_stopped.Id.ToString());
    }

    [Fact]
    public async Task The_notification_is_archived_when_the_channel_is_removed_from_its_rule()
    {
        await CheckAsync();

        _channels = [_inAppToSitter, _sending];
        await CheckAsync();

        _archived.Should().BeEquivalentTo(Owners.Select(o => (o, _stopped.Id.ToString())));
    }

    [Fact]
    public async Task The_notification_is_archived_when_its_rule_is_no_longer_enabled()
    {
        await CheckAsync();

        await Notice().CheckAsync([], _clock.GetUtcNow().UtcDateTime, CancellationToken.None);

        _archived.Select(a => a.SourceId).Should().BeEquivalentTo(
            Owners.SelectMany(_ => new[] { _inAppToSitter.Id.ToString(), _stopped.Id.ToString() }));
    }

    [Fact]
    public async Task A_member_or_adder_who_was_only_removed_is_still_named()
    {
        await CheckAsync();

        _metadata[_inAppToSitter.Id.ToString()].Should()
            .Contain(NotificationSubjectNaming.MemberSubjectIdKey, Sitter.ToString());
        _metadata[_stopped.Id.ToString()].Should()
            .Contain(NotificationSubjectNaming.AddedBySubjectIdKey, Sitter.ToString())
            .And.Contain("destinationLabel", "Family");
    }

    [Fact]
    public async Task A_notice_names_no_member_or_adder_whose_account_is_deleted_and_still_tells_the_owner()
    {
        _deleted.Add(Sitter);

        await CheckAsync();

        _created.Select(c => c.SourceId).Should().BeEquivalentTo(
            Owners.SelectMany(_ => new[] { _inAppToSitter.Id.ToString(), _stopped.Id.ToString() }));
        foreach (var metadata in _metadata.Values)
        {
            metadata.Should().NotContainKeys(
                NotificationSubjectNaming.MemberSubjectIdKey, NotificationSubjectNaming.AddedBySubjectIdKey, "destinationLabel");
            metadata!.Values.Select(v => v.ToString()).Should().NotContain(Sitter.ToString());
        }
    }

    [Fact]
    public async Task A_notice_the_deletion_removed_is_raised_again_within_freshness_without_the_name()
    {
        await CheckAsync();
        _metadata.Clear();

        _deleted.Add(Sitter);
        foreach (var notices in _active.Values)
            notices.Clear();
        PastFreshness();
        await CheckAsync();

        _metadata.Keys.Should().BeEquivalentTo([_inAppToSitter.Id.ToString(), _stopped.Id.ToString()]);
        _metadata.Values.Should().AllSatisfy(m => m.Should().NotContainKeys(
            NotificationSubjectNaming.MemberSubjectIdKey, NotificationSubjectNaming.AddedBySubjectIdKey, "destinationLabel"));
    }

    [Fact]
    public async Task A_run_that_finds_the_same_channels_reads_no_notification()
    {
        await CheckAsync();
        _created.Clear();
        foreach (var notices in _active.Values)
            notices.Clear();

        await CheckAsync();

        _created.Should().BeEmpty("the last reconcile is fresh and found the same channels");
    }
}
