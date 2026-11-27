using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Extensions;
using Xunit;
using static Nocturne.API.Tests.TestDoubles.StorageBroadcastCapture;

namespace Nocturne.API.Tests.Services.Realtime;

/// <summary>
/// The legacy documents a live write publishes reach the NS v3 storage socket of their collection; a
/// backfill, a change that names no document, and Nocturne's own settings rows do not.
/// </summary>
[Trait("Category", "Unit")]
public class LegacyStorageRecordChangeSubscriberTests
{
    private const string FoodId = "5f8d0c1e8a7b4c3d9e5f0001";

    private readonly Mock<ISignalRBroadcastService> _broadcast = new();

    private Task PublishAsync(RecordsChanged batch) =>
        new LegacyStorageRecordChangeSubscriber(_broadcast.Object).OnChangedAsync(batch, CancellationToken.None);

    private static Food Apple() => new() { Id = FoodId, Name = "apple", Carbs = 10 };

    private void VerifyNothingSent()
    {
        _broadcast.Verify(b => b.BroadcastStorageCreateAsync(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
        _broadcast.Verify(b => b.BroadcastStorageUpdateAsync(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
        _broadcast.Verify(b => b.BroadcastStorageDeleteAsync(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
    }

    public static TheoryData<object, string> Collections => new()
    {
        { Apple(), "food" },
        { new Settings { Key = "units", Value = "mmol" }, "settings" },
        { new Profile { Id = FoodId, DefaultProfile = "Default" }, "profiles" },
        { new DeviceStatus { Id = FoodId, Device = "phone" }, "devicestatus" },
    };

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task ALiveWrite_IsSentToItsCollection(object document, string collection)
    {
        await PublishAsync(new RecordsChanged(
            [new RecordTypeChange(document.GetType(), WriteOrigin.Live, [document], [document], [document])]));

        _broadcast.Verify(b => b.BroadcastStorageCreateAsync(
            collection, It.Is<StorageDocumentEvent>(e => e.ColName == collection && e.Doc == document)), Times.Once);
        _broadcast.Verify(b => b.BroadcastStorageUpdateAsync(
            collection, It.Is<StorageDocumentEvent>(e => e.ColName == collection && e.Doc == document)), Times.Once);
        _broadcast.Verify(b => b.BroadcastStorageDeleteAsync(
            collection, It.Is<StorageDeleteEvent>(e => e.ColName == collection && e.Doc == document)), Times.Once);
    }

    [Fact]
    public async Task AFoodDelete_CarriesTheFoodsIdOnTheWire()
    {
        var capture = new StorageBroadcastCapture();

        await new LegacyStorageRecordChangeSubscriber(capture.Broadcast)
            .OnChangedAsync(RecordsChanged.Of<Food>(WriteOrigin.Live, deleted: [Apple()]), CancellationToken.None);

        capture.Delete.GetProperty("colName").GetString().Should().Be("food");
        Identifier(capture.Delete).Should().Be(FoodId);
        capture.Delete.GetProperty("doc").GetProperty("name").GetString().Should().Be("apple");
    }

    [Fact]
    public async Task ABackfill_IsNotSent()
    {
        await PublishAsync(RecordsChanged.Of<Food>(WriteOrigin.Backfill, created: [Apple()], deleted: [Apple()]));

        VerifyNothingSent();
    }

    [Fact]
    public async Task AChangeThatNamesNoDocument_SendsNothing()
    {
        await PublishAsync(RecordsChanged.Written([typeof(Food), typeof(Settings)]));

        VerifyNothingSent();
    }

    [Fact]
    public async Task ARecordTypeOfNoLegacyCollection_IsNotSent()
    {
        await PublishAsync(RecordsChanged.Of<Entry>(WriteOrigin.Live, created: [new Entry { Sgv = 120 }]));

        VerifyNothingSent();
    }

    [Theory]
    [InlineData(SettingsKeys.UiSettingsPrefix + "theme")]
    [InlineData(SettingsKeys.ConnectorsPrefix + "dexcom")]
    [InlineData(SettingsKeys.SampleDataPrefix + "timeline")]
    public async Task ASettingsRowUnderAReservedKey_IsNotSent(string key)
    {
        var reserved = new Settings { Key = key, Value = "x" };

        await PublishAsync(RecordsChanged.Of<Settings>(
            WriteOrigin.Live, created: [reserved], updated: [reserved], deleted: [reserved]));

        VerifyNothingSent();
    }

    [Fact]
    public void ItIsRegisteredAsASubscriberOfTheSeam()
    {
        var services = new ServiceCollection();

        services.AddRealTimeAndNotifications(new ConfigurationBuilder().Build());

        services.Should().Contain(d =>
            d.ServiceType == typeof(IRecordChangeSubscriber)
            && d.ImplementationType == typeof(LegacyStorageRecordChangeSubscriber));
    }

    [Fact]
    public async Task ADeleteUpToTheCap_IsSentOnePerDocument()
    {
        var foods = Enumerable.Range(0, AuditedBulkDeleteExtensions.BroadcastMaterializationCap)
            .Select(i => new Food { Id = $"{i}", Name = "apple" })
            .ToList();

        await PublishAsync(RecordsChanged.Of<Food>(WriteOrigin.Live, deleted: foods));

        _broadcast.Verify(b => b.BroadcastStorageDeleteAsync("food", It.IsAny<StorageDeleteEvent>()),
            Times.Exactly(foods.Count));
        _broadcast.Verify(b => b.BroadcastStorageDeleteAsync("food", It.IsAny<StorageBulkDeleteEvent>()), Times.Never);
    }

    [Fact]
    public async Task ADeleteOverTheCap_IsSentAsOneBulkDeleteWithItsCount()
    {
        var statuses = Enumerable.Range(0, AuditedBulkDeleteExtensions.BroadcastMaterializationCap + 1)
            .Select(i => new DeviceStatus { Id = $"{i}" })
            .ToList();
        var capture = new StorageBroadcastCapture();

        await new LegacyStorageRecordChangeSubscriber(capture.Broadcast)
            .OnChangedAsync(RecordsChanged.Of<DeviceStatus>(WriteOrigin.Live, deleted: statuses), CancellationToken.None);

        capture.Delete.GetProperty("colName").GetString().Should().Be("devicestatus");
        capture.Delete.GetProperty("deletedCount").GetInt64().Should().Be(statuses.Count);
        capture.Delete.TryGetProperty("identifier", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AReservedSettingsRow_DoesNotHoldBackTheRestOfItsBatch()
    {
        var reserved = new Settings { Key = SettingsKeys.ConnectorsPrefix + "dexcom" };
        var command = new Settings { Key = "aaps_clientcontrol_wizard_c1" };

        await PublishAsync(RecordsChanged.Of<Settings>(WriteOrigin.Live, created: [reserved, command]));

        _broadcast.Verify(b => b.BroadcastStorageCreateAsync(
            "settings", It.Is<StorageDocumentEvent>(e => e.Doc == command)), Times.Once);
        _broadcast.Verify(b => b.BroadcastStorageCreateAsync(
            "settings", It.Is<StorageDocumentEvent>(e => e.Doc == reserved)), Times.Never);
    }
}
