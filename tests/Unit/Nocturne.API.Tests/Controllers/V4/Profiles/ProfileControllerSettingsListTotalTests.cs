using Nocturne.API.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// <c>GET /api/v4/profile/settings</c> totals the settings the same <c>device</c> and <c>source</c>
/// filters match, not the whole range.
/// </summary>
[Trait("Category", "Unit")]
public class ProfileControllerSettingsListTotalTests
{
    private readonly Mock<ITherapySettingsRepository> _repo = new();
    private readonly ProfileController _controller;

    public ProfileControllerSettingsListTotalTests()
    {
        _controller = new ProfileController(
            _repo.Object,
            Mock.Of<IBasalScheduleRepository>(),
            Mock.Of<ICarbRatioScheduleRepository>(),
            Mock.Of<ISensitivityScheduleRepository>(),
            Mock.Of<ITargetRangeScheduleRepository>(),
            Mock.Of<IProfileProjectionService>(),
            Mock.Of<IProfileDeletionService>(),
            TempBasalTestDeps.NoReresolution(),
            Mock.Of<ILoopClosedLoopStamper>(),
            Mock.Of<IRawMmolProfileDetector>());
    }

    [Fact]
    public async Task GetTherapySettings_IncludeTotal_CountsWithTheDeviceAndSourceTheDataQueryUses()
    {
        _repo.Setup(r => r.GetAsync(null, null, "pump-a", "tandem", 1, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TherapySettings()]);
        _repo.Setup(r => r.CountAsync(null, null, "pump-a", "tandem", It.IsAny<CancellationToken>())).ReturnsAsync(3);

        var result = await _controller.GetTherapySettings(null, null, 1, 0, "timestamp_desc", "pump-a", "tandem", includeTotal: true);

        var response = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PaginatedResponse<TherapySettings>>().Subject;
        response.Pagination.Total.Should().Be(3);
    }
}
