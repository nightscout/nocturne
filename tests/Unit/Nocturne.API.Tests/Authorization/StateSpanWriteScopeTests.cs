using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// <c>state_spans</c> holds four data categories behind one table and the caller picks which by
/// setting <see cref="StateSpan.Category"/> in the request body, so the write scope is resolved per
/// record in the handler rather than by an attribute. These drive the real handlers, because the
/// attribute sweep in <see cref="V4WriteScopeGatingTests"/> cannot see a gate that is a method call
/// — deleting the guard call would leave that sweep green.
/// </summary>
/// <seealso cref="StateSpanWriteScopeGuard"/>
public class StateSpanWriteScopeTests
{
    private static readonly string[] TreatmentsOnly = [Scope.TreatmentsReadWrite];

    [Theory]
    [InlineData(StateSpanCategory.PumpMode, Scope.DevicesReadWrite)]
    [InlineData(StateSpanCategory.PumpConnectivity, Scope.DevicesReadWrite)]
    [InlineData(StateSpanCategory.Profile, Scope.TherapyReadWrite)]
    [InlineData(StateSpanCategory.DataExclusion, Scope.GlucoseReadWrite)]
    [InlineData(StateSpanCategory.Override, Scope.TreatmentsReadWrite)]
    [InlineData(StateSpanCategory.Exercise, Scope.TreatmentsReadWrite)]
    [InlineData(StateSpanCategory.Illness, Scope.TreatmentsReadWrite)]
    [InlineData(StateSpanCategory.Travel, Scope.TreatmentsReadWrite)]
    [InlineData(StateSpanCategory.TemporaryTarget, Scope.TreatmentsReadWrite)]
    public void EveryCategory_MapsToItsDataCategoryScope(StateSpanCategory category, string expected)
    {
        StateSpanWriteScopeGuard.RequiredWriteScope(category).Should().Be(expected);
    }

    [Fact]
    public void EveryEnumMember_IsClassified()
    {
        // A category added to the enum without a scope here would otherwise inherit whichever
        // mapping happened to be first. RequiredWriteScope fails it closed to "*", and this makes
        // the omission visible instead of silently owner-only.
        Enum.GetValues<StateSpanCategory>()
            .Should().OnlyContain(c => StateSpanWriteScopeGuard.CategoryWriteScopes.ContainsKey(c));
    }

    [Fact]
    public void AnUnmappedCategory_FailsClosed()
    {
        StateSpanWriteScopeGuard.RequiredWriteScope((StateSpanCategory)9999)
            .Should().Be(Scope.FullAccess);
    }

    [Fact]
    public void FindMissingScope_ReportsTheFirstUnsatisfiedCategory()
    {
        var granted = (IReadOnlySet<string>)new HashSet<string>(TreatmentsOnly);

        StateSpanWriteScopeGuard.FindMissingScope(granted, StateSpanCategory.Exercise)
            .Should().BeNull();
        StateSpanWriteScopeGuard.FindMissingScope(granted, StateSpanCategory.DataExclusion)
            .Should().Be(Scope.GlucoseReadWrite);
    }

    [Fact]
    public void FullAccess_SatisfiesEveryCategory()
    {
        var granted = (IReadOnlySet<string>)new HashSet<string> { Scope.FullAccess };

        StateSpanWriteScopeGuard.FindMissingScope(granted, Enum.GetValues<StateSpanCategory>())
            .Should().BeNull();
    }

    // --- handler-level: the gate is a method call, so it has to be driven ---

    [Fact]
    public async Task Create_WithTreatmentsOnly_IsDeniedADataExclusionSpan()
    {
        // The one that matters: a data-exclusion window decides whether glucose readings count
        // towards analytics and reports, so a treatments-only credential marking one could hide a
        // hypo from every report.
        var service = new Mock<IStateSpanService>(MockBehavior.Strict);
        var controller = Build(service, TreatmentsOnly);

        var result = await controller.CreateStateSpan(
            new CreateStateSpanRequest { Category = StateSpanCategory.DataExclusion, StartMills = 1 });

        Denied(result.Result);
        service.Verify(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_WithTheMatchingCategoryScope_IsAllowed()
    {
        var service = new Mock<IStateSpanService>(MockBehavior.Strict);
        service.Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StateSpan { Id = "s1", Category = StateSpanCategory.DataExclusion });
        var controller = Build(service, [Scope.GlucoseReadWrite]);

        var result = await controller.CreateStateSpan(
            new CreateStateSpanRequest { Category = StateSpanCategory.DataExclusion, StartMills = 1 });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_WithTreatmentsOnly_IsAllowedATreatmentsSpan()
    {
        var service = new Mock<IStateSpanService>(MockBehavior.Strict);
        service.Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StateSpan { Id = "s1", Category = StateSpanCategory.Exercise });
        var controller = Build(service, TreatmentsOnly);

        var result = await controller.CreateStateSpan(
            new CreateStateSpanRequest { Category = StateSpanCategory.Exercise, StartMills = 1 });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Delete_ScopesOnTheStoredRecordsCategory()
    {
        // The category is not in the request, so the stored row decides. A treatments-only caller
        // must not be able to delete a pump-mode span.
        var service = new Mock<IStateSpanService>(MockBehavior.Strict);
        service.Setup(s => s.GetStateSpanByIdAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StateSpan { Id = "s1", Category = StateSpanCategory.PumpMode });
        var controller = Build(service, TreatmentsOnly);

        var result = await controller.DeleteStateSpan("s1");

        Denied(result);
        service.Verify(s => s.DeleteStateSpanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_RequiresBothTheOldAndTheNewCategory()
    {
        // Moving a span from a category the caller may write into one it may not, or vice versa,
        // needs write access to both — otherwise relocation is a way around the gate.
        var service = new Mock<IStateSpanService>(MockBehavior.Strict);
        service.Setup(s => s.GetStateSpanByIdAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StateSpan { Id = "s1", Category = StateSpanCategory.Exercise });
        var controller = Build(service, TreatmentsOnly);

        var result = await controller.UpdateStateSpan(
            "s1", new UpdateStateSpanRequest { Category = StateSpanCategory.DataExclusion });

        Denied(result.Result);
        service.Verify(
            s => s.UpdateStateSpanAsync(It.IsAny<string>(), It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Update_WithinOneAllowedCategory_IsAllowed()
    {
        var service = new Mock<IStateSpanService>(MockBehavior.Strict);
        service.Setup(s => s.GetStateSpanByIdAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StateSpan { Id = "s1", Category = StateSpanCategory.Exercise });
        service.Setup(s => s.UpdateStateSpanAsync("s1", It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StateSpan { Id = "s1", Category = StateSpanCategory.Exercise });
        var controller = Build(service, TreatmentsOnly);

        var result = await controller.UpdateStateSpan("s1", new UpdateStateSpanRequest { State = "walk" });

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    private static void Denied(object? result) =>
        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

    private static StateSpansController Build(Mock<IStateSpanService> service, string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);

        return new StateSpansController(service.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
