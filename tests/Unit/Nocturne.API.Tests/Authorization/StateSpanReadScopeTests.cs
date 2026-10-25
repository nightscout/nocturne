using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// The read side of <see cref="StateSpanWriteScopeTests"/>: the list and by-id reads narrow to the
/// categories the caller may read, and each per-category route declares its category's read scope.
/// </summary>
/// <seealso cref="StateSpanReadScopeGuard"/>
public class StateSpanReadScopeTests
{
    [Theory]
    [InlineData(StateSpanCategory.PumpMode, Scope.DevicesRead)]
    [InlineData(StateSpanCategory.PumpConnectivity, Scope.DevicesRead)]
    [InlineData(StateSpanCategory.DataExclusion, Scope.GlucoseRead)]
    [InlineData(StateSpanCategory.Override, Scope.TreatmentsRead)]
    [InlineData(StateSpanCategory.Exercise, Scope.TreatmentsRead)]
    [InlineData(StateSpanCategory.Illness, Scope.TreatmentsRead)]
    [InlineData(StateSpanCategory.Travel, Scope.TreatmentsRead)]
    [InlineData(StateSpanCategory.TemporaryTarget, Scope.TreatmentsRead)]
    [InlineData(StateSpanCategory.Motion, Scope.TreatmentsRead)]
    [InlineData(StateSpanCategory.Profile, Scope.TherapyRead)]
    public void EveryCategory_IsReadByItsDataCategoryScope(StateSpanCategory category, string expected)
    {
        StateSpanReadScopeGuard.RequiredReadScope(category).Should().Be(expected);
    }

    [Fact]
    public void EveryEnumMember_IsClassified()
    {
        Enum.GetValues<StateSpanCategory>()
            .Should().OnlyContain(c => StateSpanReadScopeGuard.CategoryReadScopes.ContainsKey(c));
    }

    [Theory]
    [InlineData(nameof(StateSpansController.GetPumpModes), StateSpanCategory.PumpMode)]
    [InlineData(nameof(StateSpansController.GetConnectivity), StateSpanCategory.PumpConnectivity)]
    [InlineData(nameof(StateSpansController.GetOverrides), StateSpanCategory.Override)]
    [InlineData(nameof(StateSpansController.GetTemporaryTargets), StateSpanCategory.TemporaryTarget)]
    [InlineData(nameof(StateSpansController.GetExercise), StateSpanCategory.Exercise)]
    [InlineData(nameof(StateSpansController.GetIllness), StateSpanCategory.Illness)]
    [InlineData(nameof(StateSpansController.GetTravel), StateSpanCategory.Travel)]
    [InlineData(nameof(StateSpansController.GetActivities), StateSpanCategory.Exercise)]
    public void EveryCategoryRoute_DeclaresItsCategorysReadScope(string action, StateSpanCategory category)
    {
        typeof(StateSpansController).GetMethod(action)!
            .GetCustomAttribute<RequireScopeAttribute>()!
            .Scopes.Should().Equal(StateSpanReadScopeGuard.RequiredReadScope(category));
    }

    [Fact]
    public async Task TheList_AsksOnlyForTheCategoriesTheCallerReads()
    {
        var service = new Mock<IStateSpanService>();
        var controller = Build(service, Scope.DevicesRead);

        await controller.GetStateSpans();

        service.Verify(s => s.GetStateSpansInCategoriesAsync(
            It.Is<IReadOnlyCollection<StateSpanCategory>>(c =>
                c.Count == 2
                && c.Contains(StateSpanCategory.PumpMode)
                && c.Contains(StateSpanCategory.PumpConnectivity)),
            null, null, null, null, null, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once());
        service.Verify(s => s.CountStateSpansInCategoriesAsync(
            It.Is<IReadOnlyCollection<StateSpanCategory>>(c => c.Count == 2),
            null, null, null, null, null, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task TheList_RefusesACategoryTheCallerCannotRead()
    {
        var service = new Mock<IStateSpanService>(MockBehavior.Strict);
        var controller = Build(service, Scope.TreatmentsRead);

        var result = await controller.GetStateSpans(category: StateSpanCategory.PumpMode);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ById_HidesASpanOfACategoryTheCallerCannotRead()
    {
        var service = new Mock<IStateSpanService>();
        service.Setup(s => s.GetStateSpanByIdAsync("span", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StateSpan { Id = "span", Category = StateSpanCategory.PumpMode });

        (await Build(service, Scope.TreatmentsRead).GetStateSpan("span"))
            .Result.Should().BeOfType<NotFoundResult>();
        (await Build(service, Scope.DevicesRead).GetStateSpan("span"))
            .Result.Should().BeOfType<OkObjectResult>();
    }

    private static StateSpansController Build(Mock<IStateSpanService> service, params string[] scopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(scopes);
        return new StateSpansController(service.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
