using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using AnchorKind = Nocturne.Infrastructure.Data.Repositories.V4.DeviceStatusGroupRepository.AnchorKind;
using AnchorRow = Nocturne.Infrastructure.Data.Repositories.V4.DeviceStatusGroupRepository.AnchorRow;
using Members = Nocturne.Infrastructure.Data.Repositories.V4.DeviceStatusGroupRepository.Members;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The member read of a device status page runs after its anchor read, so an anchor can be deleted
/// between the two.
/// </summary>
[Trait("Category", "Unit")]
public class DeviceStatusGroupMembersTests
{
    private static readonly DateTime T0 = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GroupsOf_AnAnchorWhoseRowIsGone_IsLeftOut()
    {
        var correlation = Guid.NewGuid();
        var aps = new ApsSnapshotEntity { Id = Guid.NewGuid(), CorrelationId = correlation, Timestamp = T0, SysUpdatedAt = T0 };
        var kept = new AnchorRow { Kind = AnchorKind.Aps, Id = aps.Id, CorrelationId = correlation, Timestamp = T0 };
        var gone = new AnchorRow { Kind = AnchorKind.Pump, Id = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), Timestamp = T0 };

        var groups = new Members([aps], [], [], []).GroupsOf([gone, kept]).ToList();

        groups.Select(g => g.Anchor).Should().Equal(kept);
        groups.Single().Group.Aps!.Id.Should().Be(aps.Id);
    }

    [Fact]
    public void GroupsOf_StampsAStatusByItsNewestRow()
    {
        var correlation = Guid.NewGuid();
        var pump = new PumpSnapshotEntity { Id = Guid.NewGuid(), CorrelationId = correlation, Timestamp = T0, SysUpdatedAt = T0 };
        var removedAps = new ApsSnapshotEntity
        {
            Id = Guid.NewGuid(), CorrelationId = correlation, Timestamp = T0,
            SysUpdatedAt = T0.AddHours(1), DeletedAt = T0.AddHours(1),
        };
        var anchor = new AnchorRow { Kind = AnchorKind.Pump, Id = pump.Id, CorrelationId = correlation, Timestamp = T0 };

        var group = new Members([removedAps], [pump], [], []).GroupsOf([anchor]).Single().Group;

        group.Aps.Should().BeNull("a live status does not carry a deleted row");
        group.ModifiedAt.Should().Be(T0.AddHours(1));
    }
}
