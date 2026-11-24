using FluentAssertions;
using Moq;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A direct-message channel is stamped with the member its linked account belongs to. The stamp only
/// names the member for the channel's "no recipient" state and never delivers, so it is written
/// whether or not the member's subject is active, and a rule saved again while the member is
/// deactivated does not lose it.
/// </summary>
[Trait("Category", "Unit")]
public class AlertRuleChannelWriterTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private const string Account = "123456789012345678";

    private static async Task<AlertRuleChannelWrite> WriteAsync(bool subjectIsActive)
    {
        var member = Guid.CreateVersion7();
        await using var db = TestDbContextFactory.CreateInMemoryContext($"channel_writer_{Guid.NewGuid()}");
        db.TenantId = Tenant;
        db.Subjects.Add(new SubjectEntity { Id = member, Name = "Member", IsActive = subjectIsActive });
        db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
        {
            Id = Guid.CreateVersion7(), Platform = "discord", PlatformUserId = Account, TenantId = Tenant,
            NocturneUserId = member, Label = "home", DisplayName = "Home",
        });
        await db.SaveChangesAsync();

        var writes = await new AlertRuleChannelWriter(Mock.Of<ISecretEncryptionService>()).ToWritesAsync(
            [new CreateAlertRuleChannelRequest { ChannelType = ChannelType.DiscordDm, Destination = Account }],
            saverId: null, saverAccessEndsAt: null, db, CancellationToken.None);

        var write = writes.Single();
        write.RecipientSubjectId.Should().Be(member);
        return write;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_direct_message_channel_names_the_linked_member_whether_or_not_their_subject_is_active(
        bool subjectIsActive) =>
        await WriteAsync(subjectIsActive);
}
