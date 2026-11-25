using FluentAssertions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class LowCoverageTests
{
    private static readonly IReadOnlyList<RustValidationIssue> NoIssues = [];
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime Now = new(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Live = Guid.CreateVersion7();
    private static readonly Guid Departed = Guid.CreateVersion7();
    private const string LiveDiscord = "123456789012345678";
    private const string DepartedDiscord = "223456789012345678";
    private const string UnlinkedDiscord = "323456789012345678";

    private static AlertRuleChannelSnapshot Webhook(DateTime? stopsAt = null) =>
        new(Guid.NewGuid(), Guid.Empty, ChannelType.Webhook, "https://example.test/hook", null, 0,
            AddedBySubjectId: stopsAt is null ? null : Live, StopsAt: stopsAt);

    private static AlertRuleChannelSnapshot InApp(Guid subject) =>
        new(Guid.NewGuid(), Guid.Empty, ChannelType.InApp, subject.ToString(), null, 0);

    private static AlertRuleChannelSnapshot DiscordDm(string platformUserId) =>
        new(Guid.NewGuid(), Guid.Empty, ChannelType.DiscordDm, platformUserId, null, 0);

    private static readonly AlertRuleChannelSnapshot Active = Webhook();
    private static readonly AlertRuleChannelSnapshot Stopped = Webhook(Now.AddMinutes(-1));
    private static readonly AlertRuleChannelSnapshot NoRecipient = InApp(Departed);

    private static AlertRuleRecord Rule(
        RuleScopeClass scopeClass = RuleScopeClass.Low,
        bool enabled = true,
        AlertRuleSeverity severity = AlertRuleSeverity.Warning)
    {
        var id = Guid.NewGuid();
        return new AlertRuleRecord(
            new AlertRuleSnapshot(
                id, Tenant, "Rule", AlertConditionType.Threshold, "{}", severity, "{}", 0,
                false, null, ScopeClass: scopeClass),
            null,
            enabled,
            null,
            null,
            [Active]);
    }

    private static AlertRuleRecord RuleWith(params AlertRuleChannelSnapshot[] channels) =>
        new(Rule().Rule, null, true, null, null, channels);

    /// <summary>
    /// The states at <see cref="Now"/> of a tenant where <see cref="Live"/> is a member who may
    /// receive, <see cref="Departed"/> was removed, and each has linked a Discord account.
    /// </summary>
    private static async Task<AlertChannelStates> StatesAsync(params AlertRuleRecord[] rules)
    {
        var db = TestDbContextFactory.CreateInMemoryContext($"low_coverage_{Guid.NewGuid()}");
        db.TenantId = Tenant;
        db.Subjects.AddRange(
            new SubjectEntity { Id = Live, Name = "Live" }, new SubjectEntity { Id = Departed, Name = "Departed" });
        db.ChatIdentityDirectory.AddRange(
            Link(LiveDiscord, Live),
            Link(DepartedDiscord, Departed));
        await db.SaveChangesAsync();

        var members = new Mock<ITenantMembershipRepository>();
        members.Setup(m => m.ListLivePeopleAsync(Tenant, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Member(Live)]);

        return await AlertChannelStates.LoadAsync(
            rules.SelectMany(r => r.Channels), Tenant, Now, members.Object, new TestNocturneDbContextFactory(db),
            CancellationToken.None);
    }

    private static ChatIdentityDirectoryEntry Link(string platformUserId, Guid subject) => new()
    {
        Id = Guid.CreateVersion7(),
        Platform = "discord",
        PlatformUserId = platformUserId,
        TenantId = Tenant,
        NocturneUserId = subject,
        Label = platformUserId,
        DisplayName = platformUserId,
    };

    private static TenantMemberRecord Member(Guid subject) => new(
        Id: Guid.CreateVersion7(), TenantId: Tenant, SubjectId: subject, SubjectName: "Live",
        SubjectUsername: null, SubjectIsActive: true, IsSystemSubject: false, IsDemoSubject: false,
        IsPlatformAdmin: false, HasConfirmedEmail: true,
        Roles: [new TenantMemberRoleRecord(Guid.CreateVersion7(), "Caretaker", "caretaker", ["*"])],
        DirectPermissions: null, Label: null, Username: null, LimitTo24Hours: false, AccessEndsAt: null,
        LastUsedAt: null, CreatedFromInviteId: null, SysCreatedAt: Now, Reasons: []);

    private static async Task<bool> CoversAsync(AlertRuleRecord rule, IReadOnlyList<RustValidationIssue>? issues) =>
        LowCoverage.Covers(rule, issues, await StatesAsync(rule));

    [Fact]
    public async Task A_rule_whose_issues_could_not_be_determined_does_not_cover_lows()
    {
        (await CoversAsync(Rule(), null)).Should().BeFalse();
    }

    [Fact]
    public async Task A_rule_with_all_four_conditions_covers_lows()
    {
        (await CoversAsync(Rule(), NoIssues)).Should().BeTrue();
    }

    [Theory]
    [InlineData(RuleScopeClass.Low)]
    [InlineData(RuleScopeClass.Composite)]
    public async Task A_low_or_composite_rule_covers_lows(RuleScopeClass scopeClass)
    {
        (await CoversAsync(Rule(scopeClass), NoIssues)).Should().BeTrue();
    }

    [Theory]
    [InlineData(RuleScopeClass.High)]
    [InlineData(RuleScopeClass.Undirected)]
    public async Task A_high_or_undirected_rule_does_not_cover_lows(RuleScopeClass scopeClass)
    {
        (await CoversAsync(Rule(scopeClass), NoIssues)).Should().BeFalse();
    }

    [Fact]
    public async Task A_disabled_rule_does_not_cover_lows()
    {
        (await CoversAsync(Rule(enabled: false), NoIssues)).Should().BeFalse();
    }

    [Fact]
    public async Task A_rule_with_no_channel_does_not_cover_lows()
    {
        (await CoversAsync(RuleWith(), NoIssues)).Should().BeFalse();
    }

    [Fact]
    public async Task A_rule_whose_body_cannot_be_evaluated_does_not_cover_lows()
    {
        var issue = new RustValidationIssue("condition", "threshold", "field_missing", "value");

        (await CoversAsync(Rule(), [issue])).Should().BeFalse();
    }

    [Theory]
    [InlineData("auto_resolve")]
    [InlineData("snooze")]
    public async Task A_problem_outside_the_body_leaves_the_rule_covering_lows(string scope)
    {
        var issue = new RustValidationIssue(scope, "threshold", "field_missing", "value");

        (await CoversAsync(Rule(), [issue])).Should().BeTrue();
    }

    [Theory]
    [InlineData(AlertRuleSeverity.Info)]
    [InlineData(AlertRuleSeverity.Warning)]
    [InlineData(AlertRuleSeverity.Critical)]
    public async Task Severity_does_not_decide_coverage(AlertRuleSeverity severity)
    {
        (await CoversAsync(Rule(severity: severity), NoIssues)).Should().BeTrue();
    }

    public static TheoryData<string, AlertRuleChannelSnapshot[]> ChannelsThatCover => new()
    {
        { "one channel that sends", [Active] },
        { "a typed-in channel before its stop", [Webhook(Now.AddMinutes(1))] },
        { "an in-app channel to a member who may receive", [InApp(Live)] },
        { "a direct message to a live member's linked account", [DiscordDm(LiveDiscord)] },
        { "a stopped channel beside one that sends", [Stopped, Active] },
        { "a channel without a recipient beside one that sends", [NoRecipient, Active] },
        { "every state", [Stopped, NoRecipient, DiscordDm(LiveDiscord)] },
    };

    [Theory]
    [MemberData(nameof(ChannelsThatCover))]
    public async Task A_rule_covers_lows_while_one_channel_sends(string because, AlertRuleChannelSnapshot[] channels)
    {
        (await CoversAsync(RuleWith(channels), NoIssues)).Should().BeTrue(because);
    }

    public static TheoryData<string, AlertRuleChannelSnapshot[]> ChannelsThatDoNotCover => new()
    {
        { "a typed-in channel past its stop", [Stopped] },
        { "an in-app channel to a removed member", [NoRecipient] },
        { "a direct message to an account linked to a removed member", [DiscordDm(DepartedDiscord)] },
        { "a direct message to an account no member has linked", [DiscordDm(UnlinkedDiscord)] },
        { "only stopped channels and channels without a recipient", [Stopped, NoRecipient, DiscordDm(UnlinkedDiscord)] },
    };

    [Theory]
    [MemberData(nameof(ChannelsThatDoNotCover))]
    public async Task A_rule_whose_channels_all_do_not_send_does_not_cover_lows(
        string because, AlertRuleChannelSnapshot[] channels)
    {
        (await CoversAsync(RuleWith(channels), NoIssues)).Should().BeFalse(because);
    }

    [Fact]
    public async Task Only_the_rules_that_cover_are_listed()
    {
        var covering = Rule();
        var high = Rule(RuleScopeClass.High);
        var stopped = RuleWith(Stopped);

        LowCoverage.CoveringRuleIds(
                [(covering, NoIssues), (high, NoIssues), (stopped, NoIssues)],
                await StatesAsync(covering, high, stopped))
            .Should().BeEquivalentTo([covering.Rule.Id]);
    }
}
