using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A Nightscout v3 search answers <c>{status, result}</c> and nothing about the collection's size,
/// so a search counts nothing: the socket bridge probes every collection with a one-document
/// search, and a count over a tenant's history outran it.
/// </summary>
[Trait("Category", "Unit")]
public class V3SearchTotalTests
{
    private static ControllerContext Context(string queryString) => new()
    {
        HttpContext = new DefaultHttpContext { Request = { QueryString = new QueryString(queryString) } },
    };

    [Fact]
    public async Task DeviceStatusSearch_CountsNothing()
    {
        var groups = new Mock<IDeviceStatusGroupRepository>();
        groups
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DeviceStatusGroup>());
        var controller = new DeviceStatusController(
            new DeviceStatusProjectionService(
                groups.Object,
                Mock.Of<IApsSnapshotRepository>(),
                Mock.Of<IPumpSnapshotRepository>(),
                Mock.Of<IUploaderSnapshotRepository>(),
                Mock.Of<IStateSpanRepository>(),
                Mock.Of<IDeviceStatusExtrasRepository>(),
                NullLogger<DeviceStatusProjectionService>.Instance),
            Mock.Of<IDeviceStatusDecomposer>(),
            Mock.Of<IWriteSideEffects>(),
            Mock.Of<IDataEventSink<DeviceStatus>>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<DeviceStatusController>.Instance)
        {
            ControllerContext = Context("?limit=1"),
        };

        await controller.GetDeviceStatus();

        groups.Verify(
            r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        controller.Response.Headers.Should().NotContainKey("X-Total-Count");
    }

    [Fact]
    public async Task ProfileSearch_CountsNothing()
    {
        var profiles = new Mock<IProfileProjectionService>();
        profiles
            .Setup(s => s.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Profile>());
        var controller = new ProfileController(
            profiles.Object,
            Mock.Of<IProfileWriteService>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<ProfileController>.Instance)
        {
            ControllerContext = Context("?limit=1"),
        };

        await controller.GetProfiles();

        profiles.Verify(s => s.CountProfilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        controller.Response.Headers.Should().NotContainKey("X-Total-Count");
    }

    [Fact]
    public async Task SettingsSearch_CountsNothing()
    {
        var settings = new Mock<ISettingsRepository>();
        settings
            .Setup(s => s.GetSettingsWithAdvancedFilterAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Settings>());
        var controller = new SettingsController(
            settings.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<SettingsController>.Instance)
        {
            ControllerContext = Context("?limit=1"),
        };

        await controller.GetSettings();

        settings.Verify(s => s.CountSettingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        controller.Response.Headers.Should().NotContainKey("X-Total-Count");
    }

    [Theory]
    [InlineData("?limit=1")]
    [InlineData("?limit=1&skip=2&sort=date&fields=_id")]
    public async Task PagingParameters_AreNotFieldFilters(string queryString)
    {
        TreatmentQuery? captured = null;
        var treatments = new Mock<ITreatmentService>();
        treatments
            .Setup(s => s.QueryTreatmentsAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>()))
            .Callback<TreatmentQuery, CancellationToken>((query, _) => captured = query)
            .ReturnsAsync(Array.Empty<Treatment>());
        var controller = new TreatmentsController(
            Mock.Of<IDocumentProcessingService>(),
            treatments.Object,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

        await controller.GetTreatments();

        captured.Should().NotBeNull();
        captured!.Find.Should().BeNull();
        captured.Count.Should().Be(1);
    }
}
