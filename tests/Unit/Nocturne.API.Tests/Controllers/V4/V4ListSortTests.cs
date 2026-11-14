using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Every V4 list answers a sort it does not accept with one problem response, whichever controller
/// serves it.
/// </summary>
[Trait("Category", "Unit")]
public class V4ListSortTests
{
    private const string InvalidSort = "sideways";

    public static TheoryData<string> Lists =>
    [
        nameof(StateSpansController.GetStateSpans),
        nameof(StateSpansController.GetPumpModes),
        nameof(StateSpansController.GetTravel),
        nameof(StateSpansController.GetActivities),
        nameof(SleepController.GetSessions),
        nameof(NutritionController.GetCarbIntakes),
        nameof(TempBasalController.GetAll),
        nameof(ProfileController.GetTherapySettings),
    ];

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task InvalidSort_ReturnsTheSameProblemFromEveryList(string list)
    {
        var result = await Invoke(list);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        var details = problem.Value.Should().BeAssignableTo<ProblemDetails>().Subject;
        details.Title.Should().Be("Bad Request");
        details.Detail.Should().Be("Invalid sort value 'sideways'. Must be 'timestamp_asc' or 'timestamp_desc'.");
    }

    [Theory]
    [InlineData("timestamp_desc", true)]
    [InlineData("timestamp_asc", false)]
    public void AcceptedSort_ReadsTheDirection(string sort, bool expectedDescending)
    {
        var controller = new SleepController(Mock.Of<ISleepService>());

        controller.RejectInvalidSort(sort, out var descending).Should().BeNull();

        descending.Should().Be(expectedDescending);
    }

    private static async Task<IActionResult> Invoke(string list)
    {
        var spans = new StateSpansController(Mock.Of<IStateSpanService>());
        var sleep = new SleepController(Mock.Of<ISleepService>());
        var nutrition = new NutritionController(
            Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBolusRepository>(),
            Mock.Of<ITreatmentFoodService>(),
            Mock.Of<IFoodRepository>(),
            Mock.Of<IMealReadService>());
        var tempBasal = new TempBasalController(Mock.Of<ITempBasalRepository>(), Mock.Of<IPatientDeviceStamper>());
        var profile = new ProfileController(
            Mock.Of<ITherapySettingsRepository>(),
            Mock.Of<IBasalScheduleRepository>(),
            Mock.Of<ICarbRatioScheduleRepository>(),
            Mock.Of<ISensitivityScheduleRepository>(),
            Mock.Of<ITargetRangeScheduleRepository>(),
            Mock.Of<IProfileProjectionService>(),
            Mock.Of<IProfileDeletionService>(),
            TempBasalTestDeps.NoReresolution(),
            Mock.Of<Nocturne.API.Services.V4.ILoopClosedLoopStamper>());
        foreach (var controller in new ControllerBase[] { spans, sleep, nutrition, tempBasal, profile })
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        return list switch
        {
            nameof(StateSpansController.GetStateSpans) => (await spans.GetStateSpans(sort: InvalidSort)).Result!,
            nameof(StateSpansController.GetPumpModes) => (await spans.GetPumpModes(sort: InvalidSort)).Result!,
            nameof(StateSpansController.GetTravel) => (await spans.GetTravel(sort: InvalidSort)).Result!,
            nameof(StateSpansController.GetActivities) => (await spans.GetActivities(sort: InvalidSort)).Result!,
            nameof(SleepController.GetSessions) => (await sleep.GetSessions(sort: InvalidSort)).Result!,
            nameof(NutritionController.GetCarbIntakes) => (await nutrition.GetCarbIntakes(null, null, sort: InvalidSort)).Result!,
            nameof(TempBasalController.GetAll) => (await tempBasal.GetAll(null, null, sort: InvalidSort)).Result!,
            nameof(ProfileController.GetTherapySettings) => (await profile.GetTherapySettings(null, null, sort: InvalidSort)).Result!,
            _ => throw new ArgumentOutOfRangeException(nameof(list), list, null),
        };
    }
}
