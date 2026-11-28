using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Platform;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// A clock link serves what its face shows, read as the face's owner may read it now.
/// </summary>
[Trait("Category", "Unit")]
public class ClockFaceDataControllerTests
{
    private readonly Mock<IClockFaceService> _faces = new();
    private readonly Mock<ITenantMemberService> _members = new();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _tenant = Guid.NewGuid();

    // Strict: a part the owner may not read must never be read at all.
    private readonly Mock<IIobCobReader> _iobCob = new(MockBehavior.Strict);
    private readonly Mock<ITrackerRepository> _trackers = new(MockBehavior.Strict);
    private readonly Mock<IChartDataService> _charts = new(MockBehavior.Strict);

    [Fact]
    public async Task AnUnknownFace_IsNotFound()
    {
        var id = Guid.NewGuid();

        (await Controller().GetClockFaceData(id)).Result.Should().BeOfType<NotFoundResult>();
        (await Controller().GetClockFaceChart(id)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task AFaceWhoseOwnerLostTheScope_ServesNothingOfThePart()
    {
        var id = FaceOf(_owner.ToString(), new ClockElement { Type = "iob" }, new ClockElement { Type = "trackers" });
        OwnerHolds(Scope.GlucoseRead);

        var result = await Controller().GetClockFaceData(id);

        var data = result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ClockFaceData>().Subject;
        data.Iob.Should().BeNull();
        data.Trackers.Should().BeEmpty();
    }

    [Fact]
    public async Task AFaceWhoseOwnerLeft_ServesNothingOfThePart()
    {
        var id = FaceOf(Guid.NewGuid().ToString(), new ClockElement { Type = "trackers" });

        var result = await Controller().GetClockFaceData(id);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ClockFaceData>().Which.Trackers.Should().BeEmpty();
    }

    [Fact]
    public async Task AFaceWhoseOwnerReadsOnlyThroughATeam_ServesNothing()
    {
        var id = FaceOf(_owner.ToString(), new ClockElement { Type = "iob" }, new ClockElement { Type = "trackers" });
        OwnerIs(Access(false, Scope.GlucoseRead, Scope.TreatmentsRead, Scope.AlertsRead), direct: null);

        var result = await Controller().GetClockFaceData(id);

        var data = result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ClockFaceData>().Subject;
        data.Iob.Should().BeNull();
        data.Trackers.Should().BeEmpty();
    }

    [Fact]
    public async Task AFaceWhoseOwnerReadsAPartOnlyThroughATeam_ServesNothingOfThatPart()
    {
        var id = FaceOf(_owner.ToString(), new ClockElement { Type = "iob" }, new ClockElement { Type = "trackers" });
        OwnerIs(
            sum: Access(false, Scope.GlucoseRead, Scope.TreatmentsRead, Scope.AlertsRead),
            direct: Access(false, Scope.GlucoseRead, Scope.AlertsRead));
        _trackers.Setup(t => t.GetActiveInstancesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var result = await Controller().GetClockFaceData(id);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ClockFaceData>().Which.Iob.Should().BeNull();
        _trackers.VerifyAll();
    }

    [Fact]
    public async Task AnOwnerWhoseDirectReasonIsClamped_IsClampedThroughTheirFace()
    {
        var id = FaceOf(_owner.ToString(), new ClockElement { Type = "sg" });
        OwnerIs(sum: Access(false, Scope.GlucoseRead), direct: Access(true, Scope.GlucoseRead));
        var reader = new Mock<ICategoryReadContext>();
        var controller = Controller();
        controller.HttpContext.RequestServices =
            new ServiceCollection().AddSingleton(reader.Object).BuildServiceProvider();

        await controller.GetClockFaceData(id);

        reader.Verify(r => r.ClampMemberHistory(), Times.Once());
    }

    public static TheoryData<string, string[], string[]?> OwnersWhoMayNotGiveTheChart => new()
    {
        { "an owner whose treatments access is gone", [Scope.GlucoseRead], [Scope.GlucoseRead] },
        { "an owner who reads treatments only through a team", [Scope.GlucoseRead, Scope.TreatmentsRead], [Scope.GlucoseRead] },
        { "an owner who reads everything only through a team", [Scope.GlucoseRead, Scope.TreatmentsRead], null },
    };

    [Theory]
    [MemberData(nameof(OwnersWhoMayNotGiveTheChart))]
    public async Task AChartDrawingTreatments_IsNotRead_WhenTheOwnerMayNotGiveThem(string _, string[] sum, string[]? direct)
    {
        var id = FaceOf(_owner.ToString(), new ClockElement { Type = "chart", ChartConfig = new ClockChartConfig { ShowBolus = true } });
        OwnerIs(Access(false, sum), direct is null ? null : Access(false, direct));

        var result = await Controller().GetClockFaceChart(id);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ClockFaceChart>().Which.Chart.Should().BeNull();
        _charts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AChartDrawingTreatments_IsRead_WhenTheOwnerMayGiveThem()
    {
        var id = FaceOf(_owner.ToString(), new ClockElement { Type = "chart", ChartConfig = new ClockChartConfig { ShowBolus = true } });
        OwnerHolds(Scope.GlucoseRead, Scope.TreatmentsRead);
        _charts.Setup(c => c.GetDashboardChartDataAsync(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardChartData());

        var result = await Controller().GetClockFaceChart(id);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ClockFaceChart>().Which.Chart.Should().NotBeNull();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task AHistoryClampedOwnersFace_ReadsOnlyTheLast24Hours(bool limitTo24Hours, bool clamped)
    {
        var id = FaceOf(_owner.ToString(), new ClockElement { Type = "sg" });
        OwnerIs(new TenantMemberAccess(new HashSet<string> { Scope.GlucoseRead }, limitTo24Hours, null));
        var reader = new Mock<ICategoryReadContext>();
        var controller = Controller();
        controller.HttpContext.RequestServices =
            new ServiceCollection().AddSingleton(reader.Object).BuildServiceProvider();

        await controller.GetClockFaceData(id);

        reader.Verify(r => r.ClampMemberHistory(), clamped ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task AnOwnerExemptFromTheClamp_IsNotClampedThroughTheirFace()
    {
        var id = FaceOf(_owner.ToString(), new ClockElement { Type = "sg" });
        OwnerIs(new TenantMemberAccess(new HashSet<string> { Scope.TenantSettings }, true, null));
        var reader = new Mock<ICategoryReadContext>();
        var controller = Controller();
        controller.HttpContext.RequestServices =
            new ServiceCollection().AddSingleton(reader.Object).BuildServiceProvider();

        await controller.GetClockFaceData(id);

        reader.Verify(r => r.ClampMemberHistory(), Times.Never());
    }

    [Fact]
    public void TheEditor_IsToldWhichPartsItCannotAdd()
    {
        var controller = Controller();
        controller.HttpContext.SetGrantedScopes(new HashSet<string> { Scope.GlucoseRead, Scope.SharingManage });

        var parts = controller.GetClockFaceParts().Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<ClockPartAccessDto[]>().Subject;

        string? Missing(string type, string? feature = null) =>
            parts.Single(p => p.Type == type && p.Feature == feature).MissingScope;
        Missing("summary").Should().BeNull();
        Missing("chart").Should().BeNull();
        Missing("iob").Should().Be(Scope.TreatmentsRead);
        Missing("trackers").Should().Be(Scope.AlertsRead);
        Missing("chart", "showBolus").Should().Be(Scope.TreatmentsRead);
        Missing("chart", "showDeviceEvents").Should().Be(Scope.DevicesRead);
    }

    private Guid FaceOf(string userId, params ClockElement[] elements)
    {
        var id = Guid.NewGuid();
        _faces.Setup(f => f.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClockFace
            {
                Id = id,
                UserId = userId,
                Config = new ClockFaceConfig { Rows = [new ClockRow { Elements = [.. elements] }] },
            });
        return id;
    }

    private void OwnerHolds(params string[] permissions) =>
        OwnerIs(new TenantMemberAccess(new HashSet<string>(permissions), false, null));

    // A membership whose only reason is direct reads its direct reason as the sum itself.
    private void OwnerIs(TenantMemberAccess sum) => OwnerIs(sum, sum);

    private void OwnerIs(TenantMemberAccess sum, TenantMemberAccess? direct) =>
        _members.Setup(m => m.GetMembershipFactsAsync(_owner, _tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantMembershipFacts(Guid.NewGuid(), sum, direct, null));

    private static TenantMemberAccess Access(bool limitTo24Hours, params string[] permissions) =>
        new(new HashSet<string>(permissions), limitTo24Hours, null);

    private ClockFaceDataController Controller()
    {
        var data = new ClockFaceDataService(
            _iobCob.Object, _charts.Object, Mock.Of<ISensorGlucoseRepository>(),
            Mock.Of<IStatisticsService>(), _trackers.Object, new ConfigurationBuilder().Build(),
            TimeProvider.System, NullLogger<ClockFaceDataService>.Instance);
        return new ClockFaceDataController(_faces.Object, data, _members.Object, SettableTenantAccessor.Resolved(_tenant))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection()
                        .AddSingleton(Mock.Of<ICategoryReadContext>()).BuildServiceProvider(),
                },
            },
        };
    }
}
