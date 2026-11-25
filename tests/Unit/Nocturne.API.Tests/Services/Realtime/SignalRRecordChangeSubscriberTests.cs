using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Unit.Services.Realtime;

/// <summary>
/// <see cref="SignalRRecordChangeSubscriber"/> turns a live record change into
/// <c>BroadcastStorage*Async(category, {colName, recordType, doc|id})</c> calls. The anonymous payload
/// is inspected by reflection so a payload-shape regression fails here.
/// </summary>
public class SignalRRecordChangeSubscriberTests
{
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly List<string> _sent = [];

    public SignalRRecordChangeSubscriberTests()
    {
        _broadcast.Setup(b => b.BroadcastStorageCreateAsync(It.IsAny<string>(), It.IsAny<object>()))
            .Callback(() => _sent.Add("create")).Returns(Task.CompletedTask);
        _broadcast.Setup(b => b.BroadcastStorageUpdateAsync(It.IsAny<string>(), It.IsAny<object>()))
            .Callback(() => _sent.Add("update")).Returns(Task.CompletedTask);
        _broadcast.Setup(b => b.BroadcastStorageDeleteAsync(It.IsAny<string>(), It.IsAny<object>()))
            .Callback(() => _sent.Add("delete")).Returns(Task.CompletedTask);
    }

    private Task PublishAsync<TModel>(
        WriteOrigin origin,
        IReadOnlyList<TModel>? created = null,
        IReadOnlyList<TModel>? updated = null,
        IReadOnlyList<TModel>? deleted = null) where TModel : class, IV4Record =>
        new SignalRRecordChangeSubscriber(_broadcast.Object).OnChangedAsync(
            new RecordsChanged(
            [
                new RecordTypeChange(typeof(TModel), origin, created ?? [], updated ?? [], deleted ?? []),
            ]),
            CancellationToken.None);

    private static object? GetProp(object payload, string name)
        => payload.GetType().GetProperty(name)!.GetValue(payload);

    [Fact]
    public void It_announces_after_the_caches_are_cleared()
    {
        new SignalRRecordChangeSubscriber(_broadcast.Object).Stage.Should().Be(RecordChangeStage.Announce);
    }

    [Fact]
    public async Task A_live_create_of_a_mapped_type_broadcasts_a_create_with_record_type_and_doc()
    {
        var bolus = new Bolus { Id = Guid.NewGuid(), Insulin = 5.0 };

        await PublishAsync<Bolus>(WriteOrigin.Live, created: [bolus]);

        _broadcast.Verify(b => b.BroadcastStorageCreateAsync(
            RealtimeCategories.Care,
            It.Is<object>(p => (string)GetProp(p, "colName")! == RealtimeCategories.Care
                               && (string)GetProp(p, "recordType")! == "bolus"
                               && ReferenceEquals(GetProp(p, "doc"), bolus))),
            Times.Once);
        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_live_update_of_a_mapped_type_broadcasts_an_update_with_record_type_and_doc()
    {
        var bolus = new Bolus { Id = Guid.NewGuid(), Insulin = 4.6 };

        await PublishAsync<Bolus>(WriteOrigin.Live, updated: [bolus]);

        _broadcast.Verify(b => b.BroadcastStorageUpdateAsync(
            RealtimeCategories.Care,
            It.Is<object>(p => (string)GetProp(p, "colName")! == RealtimeCategories.Care
                               && (string)GetProp(p, "recordType")! == "bolus"
                               && ReferenceEquals(GetProp(p, "doc"), bolus))),
            Times.Once);
        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_live_delete_of_a_mapped_type_broadcasts_a_delete_with_record_type_and_id()
    {
        var bolus = new Bolus { Id = Guid.NewGuid() };

        await PublishAsync<Bolus>(WriteOrigin.Live, deleted: [bolus]);

        _broadcast.Verify(b => b.BroadcastStorageDeleteAsync(
            RealtimeCategories.Care,
            It.Is<object>(p => (string)GetProp(p, "colName")! == RealtimeCategories.Care
                               && (string)GetProp(p, "recordType")! == "bolus"
                               && (Guid)GetProp(p, "id")! == bolus.Id)),
            Times.Once);
        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_change_sends_its_creates_then_its_updates_then_its_deletes()
    {
        await PublishAsync<Bolus>(
            WriteOrigin.Live,
            created: [new Bolus { Id = Guid.NewGuid() }],
            updated: [new Bolus { Id = Guid.NewGuid() }],
            deleted: [new Bolus { Id = Guid.NewGuid() }]);

        _sent.Should().Equal("create", "update", "delete");
    }

    [Fact]
    public async Task A_device_snapshot_broadcasts_to_the_device_category()
    {
        var snapshot = new ApsSnapshot { Id = Guid.NewGuid() };

        await PublishAsync<ApsSnapshot>(WriteOrigin.Live, created: [snapshot]);

        _broadcast.Verify(b => b.BroadcastStorageCreateAsync(
            RealtimeCategories.Device,
            It.Is<object>(p => (string)GetProp(p, "colName")! == RealtimeCategories.Device
                               && (string)GetProp(p, "recordType")! == "apsSnapshot"
                               && ReferenceEquals(GetProp(p, "doc"), snapshot))),
            Times.Once);
        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_device_event_broadcasts_to_the_device_category()
    {
        var deviceEvent = new DeviceEvent { Id = Guid.NewGuid() };

        await PublishAsync<DeviceEvent>(WriteOrigin.Live, created: [deviceEvent]);

        _broadcast.Verify(b => b.BroadcastStorageCreateAsync(
            RealtimeCategories.Device,
            It.Is<object>(p => (string)GetProp(p, "colName")! == RealtimeCategories.Device
                               && (string)GetProp(p, "recordType")! == "deviceEvent"
                               && ReferenceEquals(GetProp(p, "doc"), deviceEvent))),
            Times.Once);
        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_unmapped_type_sends_nothing()
    {
        var schedule = new BasalSchedule { Id = Guid.NewGuid() };

        await PublishAsync<BasalSchedule>(WriteOrigin.Live, created: [schedule], updated: [schedule], deleted: [schedule]);

        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_backfill_change_sends_nothing()
    {
        var bolus = new Bolus { Id = Guid.NewGuid() };

        await PublishAsync<Bolus>(WriteOrigin.Backfill, created: [bolus], updated: [bolus], deleted: [bolus]);

        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_change_with_no_records_sends_nothing()
    {
        await PublishAsync<Bolus>(WriteOrigin.Live);

        _broadcast.VerifyNoOtherCalls();
    }

    /// <summary>The wire bytes the remarks on <see cref="SignalRRecordChangeSubscriber"/> are about.</summary>
    [Fact]
    public async Task The_doc_reaches_the_wire_as_the_model_it_is()
    {
        var bolus = new Bolus
        {
            Id = Guid.NewGuid(),
            Timestamp = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            Insulin = 5.5,
            DataSource = "aaps",
            SyncIdentifier = "sync-1",
            Device = "pump",
        };
        object? sent = null;
        _broadcast.Setup(b => b.BroadcastStorageCreateAsync(It.IsAny<string>(), It.IsAny<object>()))
            .Callback<string, object>((_, payload) => sent = payload).Returns(Task.CompletedTask);

        await PublishAsync<Bolus>(WriteOrigin.Live, created: [bolus]);

        var wire = OnTheWire(sent!);
        wire.Should().Be(OnTheWire(new { colName = RealtimeCategories.Care, recordType = "bolus", doc = bolus }));
        wire.Should().Contain("\"insulin\":5.5").And.Contain("\"syncIdentifier\":\"sync-1\"");
    }

    private static string OnTheWire(object payload) =>
        Encoding.UTF8.GetString(new JsonHubProtocol(Options.Create(new JsonHubProtocolOptions()))
            .GetMessageBytes(new InvocationMessage("create", [payload])).Span);
}
