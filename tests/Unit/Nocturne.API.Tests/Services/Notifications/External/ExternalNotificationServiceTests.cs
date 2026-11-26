using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Notifications.External;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Notifications.External;

public sealed class ExternalNotificationServiceTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Viewer = Guid.NewGuid();

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDatabase _database = TestDbContextFactory.CreateSqlite();
    private readonly Mock<IInAppNotificationService> _notifications = new();
    private readonly Mock<ITenantMembershipRepository> _memberships = new();
    private readonly List<TenantMemberRecord> _people = [];
    private readonly Dictionary<Guid, InAppNotificationDto> _stored = [];
    private readonly List<(Guid Id, NotificationArchiveReason Reason, string UserId)> _archived = [];
    private readonly ServiceProvider _provider;

    private static readonly TenantContext Context = new(Tenant, "site", "Site", true, false);

    private static readonly ExternalNotificationSender Operator =
        ExternalNotificationSender.Operator("Nocturne.run", ["nocturne.run"]);

    private static readonly ExternalNotificationSender App =
        ExternalNotificationSender.App("client-1", "Sugarmate", Guid.NewGuid(), ["app.sugarmate.io"]);

    public ExternalNotificationServiceTests()
    {
        _database.SeedTenant(Tenant, "site");
        _people.Add(Member(Owner, RoleSeeds.Owner));
        _people.Add(Member(Admin, RoleSeeds.Admin));
        _people.Add(Member(Viewer, RoleSeeds.Viewer));

        _memberships
            .Setup(m => m.ListLivePeopleAsync(Tenant, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _people.ToList());

        _notifications
            .Setup(n => n.CreateNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Returns((string userId, string type, string title, NotificationCategory? category, NotificationUrgency? urgency,
                string? icon, string? source, string? subtitle, string? sourceId, List<NotificationActionDto>? actions,
                ResolutionConditions? conditions, Dictionary<string, object>? metadata, CancellationToken _) =>
            {
                var dto = new InAppNotificationDto
                {
                    Id = Guid.NewGuid(), Type = type, Title = title, Subtitle = subtitle,
                    Category = category!.Value, Urgency = urgency!.Value, Source = source, SourceId = sourceId,
                    Actions = actions ?? [], Metadata = metadata,
                };
                _stored[dto.Id] = dto;
                Conditions[dto.Id] = conditions;
                Recipients[dto.Id] = userId;
                return Task.FromResult(dto);
            });
        _notifications
            .Setup(n => n.ArchiveNotificationAsync(
                It.IsAny<Guid>(), It.IsAny<NotificationArchiveReason>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, NotificationArchiveReason reason, string userId, CancellationToken _) =>
            {
                _archived.Add((id, reason, userId));
                return Task.FromResult(true);
            });

        var repository = new Mock<IInAppNotificationRepository>();
        repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _stored.ContainsKey(id) ? new InAppNotification { Id = id } : null);

        var services = new ServiceCollection();
        _database.AddToServices(services);
        services.AddScoped<ITenantAccessor, SettableTenantAccessor>();
        services.AddScoped(_ => _notifications.Object);
        services.AddScoped(_ => repository.Object);
        _provider = services.BuildServiceProvider();
    }

    private Dictionary<Guid, ResolutionConditions?> Conditions { get; } = [];

    private Dictionary<Guid, string> Recipients { get; } = [];

    [Fact]
    public async Task AnOwnersNoticeReachesEveryOwnerAndNobodyElse()
    {
        var result = await SendAsync(Operator, Request("renewal", ExternalNotificationAudience.Owners));

        result.RecipientCount.Should().Be(1);
        result.Recipients.Should().ContainSingle()
            .Which.Should().Be(new ExternalNotificationRecipient(Owner, ExternalNotificationDelivery.Created, _stored.Keys.Single()));
    }

    [Fact]
    public async Task AMembersNoticeReachesEveryMember()
    {
        var result = await SendAsync(Operator, Request("maintenance", ExternalNotificationAudience.Members));

        result.Recipients.Select(r => r.SubjectId).Should().BeEquivalentTo([Owner, Admin, Viewer]);
        Recipients.Values.Should().BeEquivalentTo([Owner.ToString(), Admin.ToString(), Viewer.ToString()]);
    }

    [Fact]
    public async Task ATenantWithNoOwnerGetsAnOwnersNoticeNowhere()
    {
        _people.RemoveAll(p => p.SubjectId == Owner);

        var result = await SendAsync(Operator, Request("renewal", ExternalNotificationAudience.Owners));

        result.RecipientCount.Should().Be(0);
        result.Recipients.Should().BeEmpty();
        _stored.Should().BeEmpty("an owners notice never falls back to the members");
    }

    [Fact]
    public async Task TheOperatorMustNameTheAudience()
    {
        var refusal = await RefusedAsync(Operator, Request("renewal", audience: null));

        refusal.Should().Be(ExternalNotificationRefusal.BadRequest("audience_required", refusal.Detail));
    }

    [Fact]
    public async Task ANoticeIsAlwaysInformationThatNoDeviceMirrors()
    {
        await SendAsync(Operator, Request("renewal", ExternalNotificationAudience.Owners, link: true));

        var created = _stored.Values.Single();
        created.Urgency.Should().Be(NotificationUrgency.Info);
        created.Category.Should().Be(NotificationCategory.Informational);
        DeviceNotificationMirror.Qualifies(created).Should().BeFalse("it stays in the tray, with no banner and no push");
        created.Type.Should().Be(ExternalNotificationSender.OperatorType);
        created.Source.Should().Be(ExternalNotificationSender.OperatorId);
        created.Actions.Select(a => a.ActionId).Should().Equal("navigate", "dismiss");
        created.Metadata.Should().Contain(new Dictionary<string, object>
        {
            [ExternalNotificationSender.SenderKindKey] = "operator",
            [ExternalNotificationSender.SenderNameKey] = "Nocturne.run",
            [ExternalNotificationSender.LinkUrlKey] = "https://billing.nocturne.run/plan",
            [ExternalNotificationSender.LinkLabelKey] = "See your plan",
        });
    }

    [Fact]
    public async Task ANoticeWithNoLinkOffersOnlyDismiss()
    {
        await SendAsync(Operator, Request("renewal", ExternalNotificationAudience.Owners));

        _stored.Values.Single().Actions.Select(a => a.ActionId).Should().Equal("dismiss");
    }

    [Fact]
    public async Task ANoticeExpiresAfterThirtyDaysUnlessTheSenderSaysOtherwise()
    {
        await SendAsync(Operator, Request("a", ExternalNotificationAudience.Owners));
        var requested = _clock.GetUtcNow().AddDays(60);
        await SendAsync(Operator, Request("b", ExternalNotificationAudience.Owners) with { ExpiresAt = requested });

        Conditions.Values.Select(c => c!.ExpiresAt).Should().BeEquivalentTo(new DateTime?[]
        {
            _clock.GetUtcNow().UtcDateTime.AddDays(30),
            requested.UtcDateTime,
        });
    }

    [Fact]
    public async Task ARepeatSendReturnsTheNoticeItMadeAndSendsNothing()
    {
        var first = await SendAsync(Operator, Request("renewal", ExternalNotificationAudience.Owners));
        var second = await SendAsync(Operator, Request("renewal", ExternalNotificationAudience.Owners));

        second.Should().BeEquivalentTo(first);
        _stored.Should().ContainSingle();
    }

    [Fact]
    public async Task ARepeatSendAfterTheNoticeWasCleanedUpReportsItAlreadySent()
    {
        await SendAsync(Operator, Request("renewal", ExternalNotificationAudience.Owners));
        _stored.Clear();

        var again = await SendAsync(Operator, Request("renewal", ExternalNotificationAudience.Owners));

        again.Recipients.Should().ContainSingle()
            .Which.Should().Be(new ExternalNotificationRecipient(Owner, ExternalNotificationDelivery.AlreadySent, null));
        _stored.Should().BeEmpty();
    }

    [Fact]
    public async Task OneSendersKeyDoesNotUseUpAnothers()
    {
        await SendAsync(Operator, Request("renewal", ExternalNotificationAudience.Owners));
        await SendAsync(App with { }, Request("renewal", audience: null), grantingSubjectId: Owner);

        _stored.Should().HaveCount(2);
    }

    [Fact]
    public async Task WithdrawingArchivesTheKeyForEveryMemberItReached()
    {
        await SendAsync(Operator, Request("maintenance", ExternalNotificationAudience.Members));
        await SendAsync(Operator, Request("other", ExternalNotificationAudience.Members));
        var maintenance = _stored.Values.Where(n => n.SourceId == "maintenance").Select(n => n.Id).ToList();

        var withdrawn = await Service().WithdrawAsync(Operator, Context, "maintenance", CancellationToken.None);

        withdrawn.Result!.WithdrawnCount.Should().Be(3);
        _archived.Select(a => a.Id).Should().BeEquivalentTo(maintenance);
        _archived.Should().OnlyContain(a => a.Reason == NotificationArchiveReason.Withdrawn
            && Recipients[a.Id] == a.UserId);

        var again = await Service().WithdrawAsync(Operator, Context, "maintenance", CancellationToken.None);
        again.Result!.WithdrawnCount.Should().Be(0);

        var resent = await SendAsync(Operator, Request("maintenance", ExternalNotificationAudience.Members));
        resent.Recipients.Should().OnlyContain(r => r.Status == ExternalNotificationDelivery.Created);
        _stored.Should().HaveCount(6, "a withdrawn key stays used");
    }

    [Fact]
    public async Task WithdrawingAKeyNeverSentIsNotFound()
    {
        var outcome = await Service().WithdrawAsync(Operator, Context, "never", CancellationToken.None);

        outcome.Refusal!.StatusCode.Should().Be(404);
        outcome.Refusal.Error.Should().Be("unknown_key");
    }

    [Fact]
    public async Task AnAppNotifiesOnlyTheMemberWhoGrantedItByDefault()
    {
        var result = await SendAsync(App, Request("a", audience: null), grantingSubjectId: Viewer);

        result.Recipients.Select(r => r.SubjectId).Should().Equal(Viewer);
        _stored.Values.Single().Type.Should().Be(ExternalNotificationSender.AppType);
        _stored.Values.Single().Source.Should().Be("app:client-1");
        _stored.Values.Single().Metadata![ExternalNotificationSender.SenderKindKey].Should().Be("app");
    }

    [Theory]
    [InlineData(ExternalNotificationAudience.Owners)]
    [InlineData(ExternalNotificationAudience.Members)]
    public async Task AnAppAPlainMemberGrantedCannotNameAnAudience(ExternalNotificationAudience audience)
    {
        var refusal = await RefusedAsync(App, Request("a", audience), grantingSubjectId: Viewer);

        refusal.StatusCode.Should().Be(403);
        refusal.Error.Should().Be("audience_not_allowed");
        _stored.Should().BeEmpty();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    public async Task AnAppAnOwnerOrAdministratorGrantedMayNotifyTheMembers(string granter)
    {
        var result = await SendAsync(
            App, Request("a", ExternalNotificationAudience.Members), grantingSubjectId: granter == "owner" ? Owner : Admin);

        result.Recipients.Select(r => r.SubjectId).Should().BeEquivalentTo([Owner, Admin, Viewer]);
    }

    [Fact]
    public async Task AnAppSendsFiveNewKeysToASiteInADay()
    {
        for (var i = 0; i < ExternalNotificationSender.AppDailyLimit; i++)
        {
            await SendAsync(App, Request($"k{i}", audience: null), grantingSubjectId: Viewer);
            _clock.Advance(TimeSpan.FromHours(1));
        }

        var refusal = await RefusedAsync(App, Request("k5", audience: null), grantingSubjectId: Viewer);
        refusal.StatusCode.Should().Be(429);
        refusal.Error.Should().Be("rate_limited");
        refusal.RetryAfter.Should().Be(TimeSpan.FromHours(19), "the first send of the day leaves the window then");

        var repeat = await SendAsync(App, Request("k0", audience: null), grantingSubjectId: Viewer);
        repeat.Recipients.Should().ContainSingle("a repeat of a key already sent does not count");

        _clock.Advance(TimeSpan.FromHours(19) + TimeSpan.FromSeconds(1));
        (await SendAsync(App, Request("k5", audience: null), grantingSubjectId: Viewer)).RecipientCount.Should().Be(1);
    }

    [Fact]
    public async Task TheOperatorIsNotRateLimited()
    {
        for (var i = 0; i < 20; i++)
            await SendAsync(Operator, Request($"k{i}", ExternalNotificationAudience.Owners));

        _stored.Should().HaveCount(20);
    }

    [Fact]
    public async Task AnInactiveTenantIsAConflict()
    {
        var outcome = await Service().SendAsync(
            Operator, Context with { IsActive = false }, Request("a", ExternalNotificationAudience.Owners), null,
            CancellationToken.None);

        outcome.Refusal.Should().Be(ExternalNotificationRefusal.TenantInactive);
    }

    [Fact]
    public async Task AFailedCreateReleasesTheClaimSoTheNextSendMakesIt()
    {
        _notifications
            .SetupSequence(n => n.CreateNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("down"))
            .ReturnsAsync(new InAppNotificationDto { Id = Guid.NewGuid() });

        var failing = () => SendAsync(Operator, Request("renewal", ExternalNotificationAudience.Owners));
        await failing.Should().ThrowAsync<InvalidOperationException>();

        var retried = await SendAsync(Operator, Request("renewal", ExternalNotificationAudience.Owners));
        retried.Recipients.Should().ContainSingle().Which.Status.Should().Be(ExternalNotificationDelivery.Created);
    }

    private async Task<ExternalNotificationSendResult> SendAsync(
        ExternalNotificationSender sender, ExternalNotificationRequest request, Guid? grantingSubjectId = null)
    {
        var outcome = await Service().SendAsync(sender, Context, request, grantingSubjectId, CancellationToken.None);
        outcome.Refusal.Should().BeNull();
        return outcome.Result!;
    }

    private async Task<ExternalNotificationRefusal> RefusedAsync(
        ExternalNotificationSender sender, ExternalNotificationRequest request, Guid? grantingSubjectId = null)
    {
        var outcome = await Service().SendAsync(sender, Context, request, grantingSubjectId, CancellationToken.None);
        outcome.Result.Should().BeNull();
        return outcome.Refusal!;
    }

    private static TestRequest Request(string key, ExternalNotificationAudience? audience, bool link = false) =>
        new()
        {
            Key = key,
            Audience = audience,
            Title = "Your plan renews soon",
            Body = "It renews on 1 December.",
            Link = link ? new ExternalNotificationLink { Label = "See your plan", Url = "https://billing.nocturne.run/plan" } : null,
        };

    private ExternalNotificationService Service() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        new ExternalNotificationLedger(_provider.GetRequiredService<INocturneDbContextFactory>()),
        _memberships.Object,
        _clock,
        NullLogger<ExternalNotificationService>.Instance);

    private static TenantMemberRecord Member(Guid subjectId, string role) => new(
        Guid.NewGuid(), Tenant, subjectId, role, role, SubjectIsActive: true, IsSystemSubject: false,
        IsDemoSubject: false, IsPlatformAdmin: false, HasConfirmedEmail: false,
        Roles: [new TenantMemberRoleRecord(Guid.NewGuid(), role, role, RoleSeeds.Permissions[role])],
        DirectPermissions: null, Label: null, Username: null, LimitTo24Hours: false, AccessEndsAt: null,
        LastUsedAt: null, CreatedFromInviteId: null, SysCreatedAt: DateTime.UtcNow, Reasons: []);

    public void Dispose()
    {
        _provider.Dispose();
        _database.Dispose();
    }

    /// <summary>The request with <c>with</c> expressions, for a test that varies one field.</summary>
    private sealed record TestRequest
    {
        public string Key { get; init; } = "";
        public ExternalNotificationAudience? Audience { get; init; }
        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
        public ExternalNotificationLink? Link { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }

        public static implicit operator ExternalNotificationRequest(TestRequest r) => new()
        {
            Key = r.Key, Audience = r.Audience, Title = r.Title, Body = r.Body, Link = r.Link, ExpiresAt = r.ExpiresAt,
        };
    }
}
