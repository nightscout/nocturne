using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Services.Realtime;
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
/// The hand-written V4 lists refuse a date range over <see cref="V4ReadLimits.MaxDateSpanDays"/>
/// as the record base list does.
/// </summary>
[Trait("Category", "Unit")]
public class V4ListDateSpanTests
{
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string> Lists =>
    [
        nameof(StateSpansController.GetStateSpans),
        nameof(StateSpansController.GetPumpModes),
        nameof(StateSpansController.GetActivities),
        nameof(StateSpansController.GetTravel),
        nameof(SleepController.GetSessions),
        $"{nameof(CarbIntakeController)}.{nameof(CarbIntakeController.GetAll)}",
        nameof(TempBasalController.GetAll),
        nameof(ProfileController.GetTherapySettings),
    ];

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task ARangeOverTheCap_IsRefusedWithTheSameProblemFromEveryList(string list)
    {
        var result = await Invoke(list, From, From.AddDays(V4ReadLimits.MaxDateSpanDays + 1));

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var details = problem.Value.Should().BeAssignableTo<ProblemDetails>().Subject;
        details.Title.Should().Be("Bad Request");
        details.Detail.Should().Be($"Date range must not exceed {V4ReadLimits.MaxDateSpanDays} days.");
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task ARangeAtTheCap_IsServed(string list)
    {
        var result = await Invoke(list, From, From.AddDays(V4ReadLimits.MaxDateSpanDays));

        result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task ARangeWithOneBound_IsServed(string list)
    {
        (await Invoke(list, From, null)).Should().BeOfType<OkObjectResult>();
        (await Invoke(list, null, From)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Meals_RefuseAnOverlongExplicitSpan()
    {
        var result = await GetMeals(From, From.AddDays(V4ReadLimits.MaxDateSpanDays + 1));

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Meals_RefuseAnOldFromWithNoTo()
    {
        var result = await GetMeals(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Meals_ServeNoBounds()
    {
        (await GetMeals(null, null)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Meals_ServeASpanAtTheCap()
    {
        (await GetMeals(From, From.AddDays(V4ReadLimits.MaxDateSpanDays))).Should().BeOfType<OkObjectResult>();
    }

    private static async Task<IActionResult> GetMeals(DateTime? from, DateTime? to)
    {
        var nutrition = NewNutrition();
        return (await nutrition.GetMeals(from, to)).Result!;
    }

    private static NutritionController NewNutrition()
    {
        var mealRead = new Mock<IMealReadService>();
        mealRead
            .Setup(m => m.GetMealsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return new NutritionController(
            Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBolusRepository>(),
            Mock.Of<ITreatmentFoodService>(),
            Mock.Of<IFoodRepository>(),
            mealRead.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static async Task<IActionResult> Invoke(string list, DateTime? from, DateTime? to)
    {
        var spans = new StateSpansController(Mock.Of<IStateSpanService>(), Mock.Of<ISignalRBroadcastService>());
        var sleep = new SleepController(Mock.Of<ISleepService>());
        var carbIntakes = new CarbIntakeController(Mock.Of<ICarbIntakeRepository>(), Mock.Of<ITreatmentFoodService>());
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
            Mock.Of<Nocturne.API.Services.V4.ILoopClosedLoopStamper>(),
            Mock.Of<IRawMmolProfileDetector>());
        foreach (var controller in new ControllerBase[] { spans, sleep, carbIntakes, tempBasal, profile })
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        return list switch
        {
            nameof(StateSpansController.GetStateSpans) => (await spans.GetStateSpans(from: from, to: to)).Result!,
            nameof(StateSpansController.GetPumpModes) => (await spans.GetPumpModes(from, to)).Result!,
            nameof(StateSpansController.GetActivities) => (await spans.GetActivities(from, to)).Result!,
            nameof(StateSpansController.GetTravel) => (await spans.GetTravel(from, to)).Result!,
            nameof(SleepController.GetSessions) => (await sleep.GetSessions(from, to)).Result!,
            $"{nameof(CarbIntakeController)}.{nameof(CarbIntakeController.GetAll)}" => (await carbIntakes.GetAll(from, to)).Result!,
            nameof(TempBasalController.GetAll) => (await tempBasal.GetAll(from, to)).Result!,
            nameof(ProfileController.GetTherapySettings) => (await profile.GetTherapySettings(from, to)).Result!,
            _ => throw new ArgumentOutOfRangeException(nameof(list), list, null),
        };
    }
}
