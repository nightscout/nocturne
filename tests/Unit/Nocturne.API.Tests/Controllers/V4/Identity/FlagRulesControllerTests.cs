using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Reports;
using Nocturne.API.Services.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// <c>/api/v4/me/flag-rules</c>: the caller's own rules, refused before they reach the store when the reports validator
/// refuses their fact, held to the limit, and listed with a count of the people each cannot be checked for.
/// </summary>
[Trait("Category", "Unit")]
public class FlagRulesControllerTests
{
    private readonly Mock<IFlagRuleRepository> _rules = new();
    private readonly Mock<IFlagRuleCatalogue> _catalogue = new();
    private readonly Mock<ITenantOverviewService> _overview = new();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    private static readonly FlagRuleRequest Request = new("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4, null);

    public FlagRulesControllerTests() =>
        _catalogue.Setup(c => c.Check(It.IsAny<FlagRuleDraft>())).Returns(new Dictionary<string, string[]>());

    [Fact]
    public async Task A_rule_is_created_for_the_caller_at_warn_when_no_level_is_sent()
    {
        var created = RuleOf(Request);
        _rules.Setup(r => r.CreateAsync(_subjectId, It.IsAny<FlagRuleDraft>(), It.IsAny<CancellationToken>())).ReturnsAsync(created);

        var result = await Controller().CreateFlagRule(Request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status201Created);
        _rules.Verify(r => r.CreateAsync(_subjectId,
            new FlagRuleDraft("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4, NotificationUrgency.Warn),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_rule_the_catalogue_refuses_is_a_validation_problem_and_is_not_stored()
    {
        _catalogue.Setup(c => c.Check(It.IsAny<FlagRuleDraft>()))
            .Returns(new Dictionary<string, string[]> { ["days"] = ["Send at most 90 days."] });

        var created = await Controller().CreateFlagRule(Request, CancellationToken.None);
        var changed = await Controller().UpdateFlagRule(Guid.NewGuid(), Request, CancellationToken.None);

        foreach (var result in new[] { created, changed })
        {
            var problem = result.Should().BeAssignableTo<ObjectResult>().Which;
            problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            problem.Value.Should().BeOfType<ValidationProblemDetails>().Which.Errors.Keys.Should().Equal("days");
        }
        _rules.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_rule_past_the_limit_is_refused_with_its_code()
    {
        _rules.Setup(r => r.CreateAsync(_subjectId, It.IsAny<FlagRuleDraft>(), It.IsAny<CancellationToken>())).ReturnsAsync((FlagRule?)null);

        var result = await Controller().CreateFlagRule(Request, CancellationToken.None);

        var problem = result.Should().BeAssignableTo<ObjectResult>().Which;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        ((ProblemDetails)problem.Value!).Extensions["code"].Should().Be(FlagRulesController.LimitReachedCode);
    }

    [Fact]
    public async Task Changing_or_deleting_a_rule_the_caller_does_not_hold_is_not_found()
    {
        var ruleId = Guid.NewGuid();
        _rules.Setup(r => r.UpdateAsync(_subjectId, ruleId, It.IsAny<FlagRuleDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FlagRule?)null);
        _rules.Setup(r => r.DeleteAsync(_subjectId, ruleId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        (await Controller().UpdateFlagRule(ruleId, Request, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await Controller().DeleteFlagRule(ruleId, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_held_rule_is_changed_and_deleted()
    {
        var ruleId = Guid.NewGuid();
        var changed = RuleOf(Request);
        _rules.Setup(r => r.UpdateAsync(_subjectId, ruleId, It.IsAny<FlagRuleDraft>(), It.IsAny<CancellationToken>())).ReturnsAsync(changed);
        _rules.Setup(r => r.DeleteAsync(_subjectId, ruleId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        (await Controller().UpdateFlagRule(ruleId, Request, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(changed);
        (await Controller().DeleteFlagRule(ruleId, CancellationToken.None)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task The_list_gives_each_rule_its_count_of_people_it_cannot_check_with_the_callers_limit_and_no_names()
    {
        var held = new[] { RuleOf(Request), RuleOf(Request with { Field = "timeAbove" }) };
        IReadOnlyList<GlucoseReadTenant> people = [];
        _rules.Setup(r => r.GetOfSubjectAsync(_subjectId, It.IsAny<CancellationToken>())).ReturnsAsync(held);
        _overview.Setup(o => o.GetGlucoseReadTenantsAsync(_subjectId, It.IsAny<IReadOnlySet<string>>(), AuthType.SessionCookie,
            It.IsAny<CancellationToken>())).ReturnsAsync(people);
        _catalogue.Setup(c => c.CannotCheckCounts(held, people, true)).Returns([3, 0]);

        var result = await Controller(limitTo24Hours: true).GetFlagRules(CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<FlagRulesResponse>().Subject;
        response.Limit.Should().Be(FlagRuleLimits.PerViewer);
        response.Rules.Select(r => (r.Rule, r.CannotCheckCount)).Should().Equal((held[0], 3), (held[1], 0));
        typeof(FlagRuleSummary).GetProperties().Select(p => p.Name).Should().Equal("Rule", "CannotCheckCount");
    }

    [Fact]
    public async Task A_caller_with_no_rules_reads_no_people()
    {
        _rules.Setup(r => r.GetOfSubjectAsync(_subjectId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _catalogue.Setup(c => c.CannotCheckCounts(It.IsAny<IReadOnlyList<FlagRule>>(), It.IsAny<IReadOnlyList<GlucoseReadTenant>>(), false))
            .Returns([]);

        await Controller().GetFlagRules(CancellationToken.None);

        _overview.VerifyNoOtherCalls();
    }

    [Fact]
    public void The_facts_are_the_catalogues_and_need_no_reports_scope()
    {
        IReadOnlyList<FlagRulePreset> presets = [new("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4)];
        _catalogue.Setup(c => c.Presets).Returns(presets);
        _catalogue.Setup(c => c.Facts).Returns([]);

        var response = Controller().GetFlagRuleFacts().Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<FlagRuleFactsResponse>().Subject;

        response.Presets.Should().BeSameAs(presets);
        var gates = typeof(FlagRulesController).GetCustomAttributes(inherit: true)
            .Concat(typeof(FlagRulesController).GetMethod(nameof(FlagRulesController.GetFlagRuleFacts))!.GetCustomAttributes(inherit: true))
            .Select(a => a.GetType().Name);
        gates.Should().NotContain(name => name.Contains("Scope") || name.Contains("Permission"));
    }

    [Fact]
    public async Task A_caller_with_no_subject_is_unauthorized()
    {
        var controller = Controller(subject: false);

        (await controller.GetFlagRules(CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        (await controller.CreateFlagRule(Request, CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        _rules.VerifyNoOtherCalls();
    }

    private static FlagRule RuleOf(FlagRuleRequest request) =>
        new(Guid.CreateVersion7(), request.Dataset, request.Field, request.Days, request.Comparison!.Value, request.Value!.Value,
            request.Level ?? FlagRuleLimits.DefaultLevel, DateTime.UtcNow, DateTime.UtcNow);

    private FlagRulesController Controller(bool limitTo24Hours = false, bool subject = true)
    {
        var httpContext = new DefaultHttpContext { RequestServices = TestRequestServices.Build(clamped: false) };
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = subject ? _subjectId : null,
            LimitTo24Hours = limitTo24Hours,
        };
        return new FlagRulesController(_rules.Object, _catalogue.Object, _overview.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
