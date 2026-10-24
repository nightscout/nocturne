using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

[Trait("Category", "Unit")]
public class ProfileControllerRawMmolTests
{
    private static IActionResult? Gate(string action, params string[] scopes) =>
        ActionGate.Evaluate(
            typeof(ProfileController), action, new HashSet<string>(scopes), new AuthContext { IsAuthenticated = true });

    [Theory]
    [InlineData(Scope.TherapyRead)]
    [InlineData(Scope.GlucoseReadWrite)]
    public void TheIsfDismiss_IsRefusedWithoutTherapyReadWrite(string scope) =>
        Gate(nameof(ProfileController.DismissRawMmolIsf), scope).Should().BeOfType<ForbidResult>();

    [Fact]
    public void TheIsfDismiss_IsAdmittedWithTherapyReadWrite() =>
        Gate(nameof(ProfileController.DismissRawMmolIsf), Scope.TherapyReadWrite).Should().BeNull();

    [Fact]
    public void TheList_IsReadableWithTherapyRead() =>
        Gate(nameof(ProfileController.GetRawMmolSchedules), Scope.TherapyRead).Should().BeNull();

    [Fact]
    public async Task DismissingAScheduleTheDetectorCannotMark_IsNotFound()
    {
        var detector = new Mock<IRawMmolProfileDetector>();
        detector.Setup(d => d.DismissIsfAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var controller = new ProfileController(
            Mock.Of<ITherapySettingsRepository>(),
            Mock.Of<IBasalScheduleRepository>(),
            Mock.Of<ICarbRatioScheduleRepository>(),
            Mock.Of<ISensitivityScheduleRepository>(),
            Mock.Of<ITargetRangeScheduleRepository>(),
            Mock.Of<IProfileProjectionService>(),
            TempBasalTestDeps.NoReresolution(),
            detector.Object);

        (await controller.DismissRawMmolIsf(Guid.CreateVersion7())).Should().BeOfType<NotFoundResult>();
    }
}
