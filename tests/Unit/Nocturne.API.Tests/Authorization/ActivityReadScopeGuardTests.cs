using System.Reflection;
using FluentAssertions;
using Moq;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V1;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Tests.Authorization;

[Trait("Category", "Unit")]
public class ActivityReadScopeGuardTests
{
    private static Activity Activity(string id) => new() { Id = id, Mills = 1700000000000 };

    /// <summary>
    /// Stub decomposer mapping activity ids to a canned required read scope, so the guard's
    /// scope-checking logic is exercised without a real classifier or DbContext.
    /// </summary>
    private static IActivityDecomposer Decomposer(Dictionary<string, string> idToScope)
    {
        var mock = new Mock<IActivityDecomposer>();
        mock.Setup(d => d.RequiredReadScope(It.IsAny<Activity>()))
            .Returns((Activity a) => idToScope[a.Id!]);
        return mock.Object;
    }

    private static IReadOnlySet<string> Granted(params string[] scopes) =>
        new HashSet<string>(scopes);

    private static Dictionary<string, string> OneOfEachCategory() => new()
    {
        ["hr"] = Scope.HeartRateRead,
        ["sc"] = Scope.StepCountRead,
        ["sleep"] = Scope.SleepRead,
        ["regular"] = Scope.TreatmentsRead,
    };

    private static Activity[] OneRecordPerCategory() =>
        [Activity("hr"), Activity("sc"), Activity("sleep"), Activity("regular")];

    [Fact]
    public void Filter_TreatmentsOnlyGrant_KeepsOnlyRegularActivities()
    {
        var kept = ActivityReadScopeGuard.Filter(
            OneRecordPerCategory(), Decomposer(OneOfEachCategory()), Granted(Scope.TreatmentsRead));

        kept.Select(a => a.Id).Should().Equal("regular");
    }

    [Fact]
    public void Filter_HeartRateOnlyGrant_KeepsOnlyHeartRate()
    {
        var kept = ActivityReadScopeGuard.Filter(
            OneRecordPerCategory(), Decomposer(OneOfEachCategory()), Granted(Scope.HeartRateRead));

        kept.Select(a => a.Id).Should().Equal("hr");
    }

    [Fact]
    public void Filter_NoScopes_KeepsNothing()
    {
        var kept = ActivityReadScopeGuard.Filter(
            OneRecordPerCategory(), Decomposer(OneOfEachCategory()), Granted());

        kept.Should().BeEmpty();
    }

    [Fact]
    public void Filter_FullAccess_KeepsEveryCategory()
    {
        var kept = ActivityReadScopeGuard.Filter(
            OneRecordPerCategory(), Decomposer(OneOfEachCategory()), Granted(Scope.FullAccess));

        kept.Should().HaveCount(4);
    }

    /// <summary>
    /// A readwrite grant implies its read counterpart, so a sleep-writing client keeps reading back
    /// what it wrote.
    /// </summary>
    [Fact]
    public void Filter_ReadWriteGrant_SatisfiesTheReadCategory()
    {
        var kept = ActivityReadScopeGuard.Filter(
            OneRecordPerCategory(), Decomposer(OneOfEachCategory()), Granted(Scope.SleepReadWrite));

        kept.Select(a => a.Id).Should().Equal("sleep");
    }

    [Fact]
    public void CanRead_UnheldCategory_IsFalse()
    {
        ActivityReadScopeGuard.CanRead(
            Activity("hr"), Decomposer(OneOfEachCategory()), Granted(Scope.StepCountRead))
            .Should().BeFalse();
    }

    /// <summary>
    /// The admission list on the read actions must cover exactly the categories the merged read can
    /// return, or a caller holding one of them is refused the endpoint that serves its own data.
    /// </summary>
    [Fact]
    public void AdmissionScopes_CoverEveryMergedCategory()
    {
        ActivityReadScopeGuard.AdmissionScopes.Should().BeEquivalentTo(new[]
        {
            Scope.TreatmentsRead,
            Scope.HeartRateRead,
            Scope.StepCountRead,
            Scope.SleepRead,
        });
    }

    /// <summary>
    /// The action attributes are what the pipeline actually enforces, and the guard only runs on a
    /// request the attribute admitted. Narrowing the attribute back to a single category would refuse
    /// a caller the endpoint that serves exactly its own data.
    /// </summary>
    [Theory]
    [InlineData(nameof(ActivityController.GetActivities))]
    [InlineData(nameof(ActivityController.GetActivity))]
    public void V1ActivityReadActions_AdmitAnyMergedCategory(string actionName)
    {
        var attribute = typeof(ActivityController)
            .GetMethod(actionName)!
            .GetCustomAttribute<RequireScopeAttribute>();

        attribute.Should().NotBeNull();
        attribute!.RequiresAll.Should().BeFalse("holding one category must admit the caller");
        attribute.RequiredScopes.Should().BeEquivalentTo(ActivityReadScopeGuard.AdmissionScopes);
    }

    /// <summary>
    /// The activity count sums all four storages into one number that cannot be filtered per
    /// category, so it requires all four rather than any one. Driven through the action rather than
    /// read off an attribute, because the handler makes the check and an attribute scan cannot
    /// see it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CountActivityRoute_RequiresEveryMergedCategory(bool holdsEveryCategory)
    {
        var granted = holdsEveryCategory
            ? ActivityReadScopeGuard.AdmissionScopes.ToArray()
            : ActivityReadScopeGuard.AdmissionScopes.Take(ActivityReadScopeGuard.AdmissionScopes.Count - 1).ToArray();

        var activityService = new Mock<IActivityService>();
        activityService
            .Setup(s => s.CountActivitiesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3L);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = Scope.Normalize(granted);

        var controller = new CountController(
            Mock.Of<IEntryStore>(), Mock.Of<ITreatmentStore>(), Mock.Of<IApsSnapshotRepository>(),
            Mock.Of<IProfileProjectionService>(), Mock.Of<IFoodRepository>(),
            activityService.Object, NullLogger<CountController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var result = await controller.CountActivity();

        if (holdsEveryCategory)
        {
            result.Result.Should().NotBeOfType<ObjectResult>();
        }
        else
        {
            result.Result.Should().BeOfType<ObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            activityService.Verify(
                s => s.CountActivitiesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
    }
}
