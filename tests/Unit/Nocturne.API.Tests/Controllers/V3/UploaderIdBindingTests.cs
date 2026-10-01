using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A v3 create body names its identity with <c>_id</c>; an uploader's own <c>id</c> stays a plain
/// field, as on legacy Nightscout, whichever key comes first.
/// </summary>
[Trait("Category", "Unit")]
public class UploaderIdBindingTests
{
    private const string ObjectId = "6ab400000000000000000001";
    private const string UploaderId = "B5E5A1C2-0000-4000-8000-000000000001";

    public static TheoryData<string> KeyOrders => new()
    {
        $$"""{"_id":"{{ObjectId}}","id":"{{UploaderId}}"}""",
        $$"""{"id":"{{UploaderId}}","_id":"{{ObjectId}}"}""",
    };

    [Theory]
    [MemberData(nameof(KeyOrders))]
    public async Task A_devicestatus_is_identified_by_its_object_id(string ids)
    {
        var decomposed = new List<DeviceStatus>();
        var decomposer = new Mock<IDeviceStatusDecomposer>();
        decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<DeviceStatus>(), It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceStatus, string?, WriteOrigin, CancellationToken>((ds, _, _, _) => decomposed.Add(ds))
            .ReturnsAsync(new DecompositionResult());
        var projection = new DeviceStatusProjectionService(
            Mock.Of<IApsSnapshotRepository>(),
            Mock.Of<IPumpSnapshotRepository>(),
            Mock.Of<IUploaderSnapshotRepository>(),
            Mock.Of<IStateSpanRepository>(),
            Mock.Of<IDeviceStatusExtrasRepository>(),
            NullLogger<DeviceStatusProjectionService>.Instance);
        var controller = WithContext(new DeviceStatusController(
            projection,
            decomposer.Object,
            Mock.Of<IWriteSideEffects>(),
            Mock.Of<IDataEventSink<DeviceStatus>>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<DeviceStatusController>.Instance));

        await controller.CreateDeviceStatus(Body(ids, """ "app":"AAPS","device":"openaps://phone" """));

        var status = decomposed.Should().ContainSingle().Subject;
        status.Id.Should().Be(ObjectId);
        status.ExtensionData!["id"].GetString().Should().Be(UploaderId);
    }

    [Theory]
    [MemberData(nameof(KeyOrders))]
    public async Task A_settings_record_is_identified_by_its_object_id(string ids)
    {
        var created = new List<Settings>();
        var repository = new Mock<ISettingsRepository>();
        repository
            .Setup(r => r.CreateSettingsAsync(It.IsAny<IEnumerable<Settings>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Settings>, CancellationToken>((s, _) => created.AddRange(s))
            .ReturnsAsync([]);
        var controller = WithContext(new SettingsController(
            repository.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<SettingsController>.Instance));

        await controller.CreateSettings(Body(ids, """ "key":"units" """));

        created.Should().ContainSingle().Which.Id.Should().Be(ObjectId);
    }

    [Theory]
    [MemberData(nameof(KeyOrders))]
    public async Task A_profile_is_identified_by_its_object_id(string ids)
    {
        var created = new List<Profile>();
        var writeService = new Mock<IProfileWriteService>();
        writeService
            .Setup(w => w.CreateProfilesAsync(It.IsAny<IEnumerable<Profile>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Profile>, CancellationToken>((p, _) => created.AddRange(p))
            .ReturnsAsync([]);
        var controller = WithContext(new ProfileController(
            Mock.Of<IProfileProjectionService>(),
            writeService.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<ProfileController>.Instance));

        await controller.CreateProfile(Body(ids, """ "defaultProfile":"Default" """));

        created.Should().ContainSingle().Which.Id.Should().Be(ObjectId);
    }

    private static JsonElement Body(string ids, string fields) =>
        JsonDocument.Parse(ids.TrimEnd('}') + "," + fields + "}").RootElement.Clone();

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
}
