using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

[Trait("Category", "Unit")]
public class ActogramReadScopeGuardTests
{
    private static ActogramReportData OneRecordPerCategory() => new()
    {
        Glucose = [new GlucosePointDto { Time = 1700000000000, Sgv = 120 }],
        Thresholds = new ChartThresholdsDto { Low = 70, High = 180 },
        HeartRates = [new HeartRatePointDto { Time = 1700000000000, Bpm = 64 }],
        StepCounts = [new StepBubbleDto { Time = 1700000000000, Steps = 900 }],
        SleepSpans = [new ActogramSleepSpan { StartMills = 1, EndMills = 2, State = "deep" }],
    };

    private static IReadOnlySet<string> Granted(params string[] scopes) =>
        new HashSet<string>(scopes);

    [Fact]
    public void Redact_GlucoseOnlyGrant_KeepsGlucoseAndItsThresholds()
    {
        var data = ActogramReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.GlucoseRead));

        data.Glucose.Should().HaveCount(1);
        data.Thresholds.Low.Should().Be(70);
        data.HeartRates.Should().BeEmpty();
        data.StepCounts.Should().BeEmpty();
        data.SleepSpans.Should().BeEmpty();
    }

    [Fact]
    public void Redact_StepCountOnlyGrant_KeepsOnlyStepCounts()
    {
        var data = ActogramReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.StepCountRead));

        data.StepCounts.Should().HaveCount(1);
        data.Glucose.Should().BeEmpty();
        data.HeartRates.Should().BeEmpty();
        data.SleepSpans.Should().BeEmpty();
    }

    [Fact]
    public void Redact_HeartRateOnlyGrant_KeepsOnlyHeartRates()
    {
        var data = ActogramReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.HeartRateRead));

        data.HeartRates.Should().HaveCount(1);
        data.Glucose.Should().BeEmpty();
        data.StepCounts.Should().BeEmpty();
        data.SleepSpans.Should().BeEmpty();
    }

    /// <summary>
    /// The thresholds are read off the active therapy profile, so a caller with no glucose grant
    /// must not learn the personal target band from them either.
    /// </summary>
    [Fact]
    public void Redact_WithoutGlucose_ClearsTheThresholds()
    {
        var data = ActogramReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.SleepRead));

        data.Thresholds.Should().BeEquivalentTo(new ChartThresholdsDto());
    }

    [Fact]
    public void Redact_NoScopes_EmptiesEveryCategory()
    {
        var data = ActogramReadScopeGuard.Redact(OneRecordPerCategory(), Granted());

        data.Glucose.Should().BeEmpty();
        data.HeartRates.Should().BeEmpty();
        data.StepCounts.Should().BeEmpty();
        data.SleepSpans.Should().BeEmpty();
        data.Thresholds.Should().BeEquivalentTo(new ChartThresholdsDto());
    }

    [Fact]
    public void Redact_FullAccess_KeepsEveryCategory()
    {
        var data = ActogramReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.FullAccess));

        data.Glucose.Should().HaveCount(1);
        data.HeartRates.Should().HaveCount(1);
        data.StepCounts.Should().HaveCount(1);
        data.SleepSpans.Should().HaveCount(1);
        data.Thresholds.Low.Should().Be(70);
    }

    /// <summary>
    /// A readwrite grant implies its read counterpart, so a sleep-writing client keeps reading back
    /// what it wrote.
    /// </summary>
    [Fact]
    public void Redact_ReadWriteGrant_SatisfiesTheReadCategory()
    {
        var data = ActogramReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.SleepReadWrite));

        data.SleepSpans.Should().HaveCount(1);
        data.Glucose.Should().BeEmpty();
    }

    /// <summary>
    /// The admission list must cover exactly the categories the merged report can return, or a
    /// caller holding one of them is refused the endpoint that serves its own data.
    /// </summary>
    [Fact]
    public void AdmissionScopes_CoverEveryMergedCategory()
    {
        ActogramReadScopeGuard.AdmissionScopes.Should().BeEquivalentTo(new[]
        {
            Scope.GlucoseRead,
            Scope.HeartRateRead,
            Scope.StepCountRead,
            Scope.SleepRead,
        });
    }

    /// <summary>
    /// The action attribute is what the pipeline actually enforces, and the guard only runs on a
    /// request the attribute admitted. Requiring all four instead would 403 every public share,
    /// because <see cref="Scope.SleepRead"/> is outside
    /// <see cref="Scope.PublicShareScopes"/>.
    /// </summary>
    [Fact]
    public void GetActogram_AdmitsAnyMergedCategory()
    {
        var attribute = typeof(ActogramController)
            .GetMethod(nameof(ActogramController.GetActogram))!
            .GetCustomAttribute<RequireScopeAttribute>();

        attribute.Should().NotBeNull();
        attribute!.RequiresAll.Should().BeFalse("holding one category must admit the caller");
        attribute.RequiredScopes.Should().BeEquivalentTo(ActogramReadScopeGuard.AdmissionScopes);
    }

    /// <summary>
    /// Asserts that the handler actually calls the guard: the attribute sweep above and the guard
    /// tests would both stay green with the call removed, leaving the response unfiltered.
    /// </summary>
    [Fact]
    public async Task GetActogram_RedactsTheCategoriesTheCallerLacks()
    {
        var service = new Mock<IActogramReportService>();
        service
            .Setup(s => s.GetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OneRecordPerCategory());

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = Granted(Scope.StepCountRead);

        var controller = new ActogramController(
            service.Object, NullLogger<ActogramController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var result = await controller.GetActogram(startTime: 0, endTime: 1);

        var data = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ActogramReportData>().Subject;
        data.StepCounts.Should().HaveCount(1);
        data.Glucose.Should().BeEmpty();
        data.HeartRates.Should().BeEmpty();
        data.SleepSpans.Should().BeEmpty();
    }
}
