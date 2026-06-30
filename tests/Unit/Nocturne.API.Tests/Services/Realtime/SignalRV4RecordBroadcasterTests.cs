using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Unit.Services.Realtime;

/// <summary>
/// Unit tests for <see cref="SignalRV4RecordBroadcaster{TModel}"/> — the SignalR adapter that turns a
/// native V4 record write into a <c>BroadcastStorage*Async(category, {recordType, doc})</c> call. Mapped
/// types fan out to their category; unmapped (dormant <c>therapy</c>) types and empty lists broadcast
/// nothing. The anonymous payload is inspected by reflection so a payload-shape regression fails here.
/// </summary>
public class SignalRV4RecordBroadcasterTests
{
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();

    private SignalRV4RecordBroadcaster<TModel> Adapter<TModel>() where TModel : class
        => new(_broadcast.Object);

    private static object? GetProp(object payload, string name)
        => payload.GetType().GetProperty(name)!.GetValue(payload);

    [Fact]
    public async Task BroadcastCreatedAsync_MappedType_BroadcastsCreateWithRecordTypeAndDoc()
    {
        var bolus = new Bolus { Id = Guid.NewGuid(), Insulin = 5.0 };

        await Adapter<Bolus>().BroadcastCreatedAsync([bolus]);

        _broadcast.Verify(b => b.BroadcastStorageCreateAsync(
            RealtimeCategories.Care,
            It.Is<object>(p => (string)GetProp(p, "recordType")! == "bolus"
                               && ReferenceEquals(GetProp(p, "doc"), bolus))),
            Times.Once);
        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BroadcastUpdatedAsync_MappedType_BroadcastsUpdateWithRecordTypeAndDoc()
    {
        var bolus = new Bolus { Id = Guid.NewGuid(), Insulin = 4.6 };

        await Adapter<Bolus>().BroadcastUpdatedAsync([bolus]);

        _broadcast.Verify(b => b.BroadcastStorageUpdateAsync(
            RealtimeCategories.Care,
            It.Is<object>(p => (string)GetProp(p, "recordType")! == "bolus"
                               && ReferenceEquals(GetProp(p, "doc"), bolus))),
            Times.Once);
        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BroadcastDeletedAsync_MappedType_BroadcastsDeleteWithRecordTypeAndId()
    {
        var id = Guid.NewGuid();

        await Adapter<Bolus>().BroadcastDeletedAsync([id]);

        _broadcast.Verify(b => b.BroadcastStorageDeleteAsync(
            RealtimeCategories.Care,
            It.Is<object>(p => (string)GetProp(p, "recordType")! == "bolus"
                               && (Guid)GetProp(p, "id")! == id)),
            Times.Once);
        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BroadcastCreatedAsync_UnmappedType_DoesNotBroadcast()
    {
        await Adapter<BasalSchedule>().BroadcastCreatedAsync([new BasalSchedule { Id = Guid.NewGuid() }]);

        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BroadcastUpdatedAsync_UnmappedType_DoesNotBroadcast()
    {
        await Adapter<BasalSchedule>().BroadcastUpdatedAsync([new BasalSchedule { Id = Guid.NewGuid() }]);

        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BroadcastDeletedAsync_UnmappedType_DoesNotBroadcast()
    {
        await Adapter<BasalSchedule>().BroadcastDeletedAsync([Guid.NewGuid()]);

        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BroadcastCreatedAsync_EmptyList_DoesNotBroadcast()
    {
        await Adapter<Bolus>().BroadcastCreatedAsync([]);

        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BroadcastDeletedAsync_EmptyList_DoesNotBroadcast()
    {
        await Adapter<Bolus>().BroadcastDeletedAsync([]);

        _broadcast.VerifyNoOtherCalls();
    }
}
