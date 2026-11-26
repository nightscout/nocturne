using FluentAssertions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The one rule for whether a channel that goes to a member delivers: the member may receive, the
/// same for an <c>in_app</c> channel and a direct message to their linked account.
/// </summary>
[Trait("Category", "Unit")]
public class AlertChannelStatesTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime Now = new(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc);
    private const string Account = "123456789012345678";
    private const string WaId = "61412345678";

    private static async Task<AlertChannelStates> StatesAsync(
        Guid member, IReadOnlyList<string> permissions, bool subjectIsActive = true)
    {
        var db = TestDbContextFactory.CreateInMemoryContext($"channel_states_{Guid.NewGuid()}");
        db.TenantId = Tenant;
        db.Subjects.Add(new SubjectEntity { Id = member, Name = "Member", IsActive = subjectIsActive });
        db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
        {
            Id = Guid.CreateVersion7(), Platform = "discord", PlatformUserId = Account, TenantId = Tenant,
            NocturneUserId = member, Label = "home", DisplayName = "Home",
        });
        db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
        {
            Id = Guid.CreateVersion7(), Platform = "whatsapp", PlatformUserId = WaId, TenantId = Tenant,
            NocturneUserId = member, Label = "home", DisplayName = "Home",
        });
        await db.SaveChangesAsync();

        var members = new Mock<ITenantMembershipRepository>();
        members.Setup(m => m.ListLivePeopleAsync(Tenant, Now, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new TenantMemberRecord(
                Id: Guid.CreateVersion7(), TenantId: Tenant, SubjectId: member, SubjectName: "Member",
                SubjectUsername: null, SubjectIsActive: true, IsSystemSubject: false, IsDemoSubject: false,
                IsPlatformAdmin: false, HasConfirmedEmail: true, Roles: [], DirectPermissions: permissions,
                Label: null, Username: null, LimitTo24Hours: false, AccessEndsAt: null, LastUsedAt: null,
                CreatedFromInviteId: null, SysCreatedAt: Now, Reasons: []),
        ]);

        return await AlertChannelStates.LoadAsync(
            [InApp(member), DirectMessage()], Tenant, Now, members.Object, new TestNocturneDbContextFactory(db),
            CancellationToken.None);
    }

    private static AlertRuleChannelSnapshot InApp(Guid member) =>
        new(Guid.NewGuid(), Guid.NewGuid(), ChannelType.InApp, member.ToString(), null, 0);

    private static AlertRuleChannelSnapshot DirectMessage() =>
        new(Guid.NewGuid(), Guid.NewGuid(), ChannelType.DiscordDm, Account, null, 1);

    private static AlertRuleChannelSnapshot WhatsApp(string destination, DateTime? stopsAt = null, Guid? recipient = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), ChannelType.WhatsAppDm, destination, null, 2,
            AddedBySubjectId: Guid.CreateVersion7(), StopsAt: stopsAt, RecipientSubjectId: recipient);

    [Fact]
    public async Task A_whatsapp_number_no_link_names_delivers_until_the_access_of_the_member_who_added_it_ends()
    {
        var states = await StatesAsync(Guid.CreateVersion7(), permissions: ["*"]);

        states.Of(WhatsApp("+61499999999")).Should().Be(new AlertChannelVerdict(AlertChannelState.Active, null, null));
        states.Of(WhatsApp("+61499999999", stopsAt: Now.AddDays(1))).IsActive.Should().BeTrue();
        states.Of(WhatsApp("+61499999999", stopsAt: Now.AddDays(-1))).State.Should().Be(AlertChannelState.Stopped);
    }

    [Theory]
    [InlineData(WaId)]
    [InlineData("+" + WaId)]
    public async Task A_whatsapp_number_a_member_linked_goes_to_that_member(string destination)
    {
        var member = Guid.CreateVersion7();
        var states = await StatesAsync(member, permissions: ["*"]);

        states.Of(WhatsApp(destination, stopsAt: Now.AddDays(-1))).Should().Be(
            new AlertChannelVerdict(AlertChannelState.Active, member, null));
    }

    [Fact]
    public async Task A_whatsapp_channel_saved_to_a_linked_member_stops_when_the_link_is_gone()
    {
        var departed = Guid.CreateVersion7();
        var states = await StatesAsync(Guid.CreateVersion7(), permissions: ["*"]);

        states.Of(WhatsApp("61400000000", recipient: departed)).Should().Match<AlertChannelVerdict>(
            v => v.State == AlertChannelState.NoRecipient && v.MemberSubjectId == departed);
    }

    [Fact]
    public void A_typed_whatsapp_number_is_judged_from_its_row_when_the_members_cannot_be_read()
    {
        var states = AlertChannelStates.Unread(Now);

        states.Of(WhatsApp("+61499999999")).IsActive.Should().BeTrue();
        states.Of(WhatsApp(WaId, recipient: Guid.CreateVersion7())).State.Should().Be(AlertChannelState.NoRecipient);
    }

    [Fact]
    public async Task A_live_member_who_holds_no_access_receives_neither_in_app_nor_by_direct_message()
    {
        var member = Guid.CreateVersion7();
        var states = await StatesAsync(member, permissions: []);

        states.Of(InApp(member)).State.Should().Be(AlertChannelState.NoRecipient);
        states.Of(DirectMessage()).Should().Match<AlertChannelVerdict>(
            v => v.State == AlertChannelState.NoRecipient && v.MemberSubjectId == member);
    }

    [Fact]
    public async Task A_deactivated_subjects_linked_account_receives_no_direct_message_and_an_active_ones_does()
    {
        var deactivated = Guid.CreateVersion7();
        var active = Guid.CreateVersion7();

        (await StatesAsync(deactivated, permissions: ["*"], subjectIsActive: false))
            .Of(DirectMessage()).IsActive.Should().BeFalse();
        (await StatesAsync(active, permissions: ["*"], subjectIsActive: true))
            .Of(DirectMessage()).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task A_live_member_who_holds_access_receives_both()
    {
        var member = Guid.CreateVersion7();
        var states = await StatesAsync(member, permissions: ["*"]);

        states.Of(InApp(member)).IsActive.Should().BeTrue();
        states.Of(DirectMessage()).IsActive.Should().BeTrue();
    }
}
