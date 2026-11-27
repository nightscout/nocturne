using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Reports;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>A flag rule as it is created or changed.</summary>
/// <param name="Dataset">The dataset of the fact, as the facts list names it.</param>
/// <param name="Field">The fact.</param>
/// <param name="Days">The last N days a period fact covers; leave out for a fact as it is now.</param>
/// <param name="Comparison">Required. Nullable so a body without it is refused, not read as the first comparison.</param>
/// <param name="Value">Required, in the fact's unit: a glucose value in mg/dL. Nullable so a body without it is refused, not read as 0.</param>
/// <param name="Level">Info, Warn or Hazard; Warn when left out.</param>
public sealed record FlagRuleRequest(
    [property: Required] string Dataset,
    [property: Required] string Field,
    int? Days,
    [property: Required] FlagRuleComparison? Comparison,
    [property: Required] double? Value,
    NotificationUrgency? Level);

/// <summary>One of the caller's flag rules, and how many of the people they follow it cannot be checked for.</summary>
/// <param name="CannotCheckCount">
/// How many of the people the caller follows the rule cannot be checked for: the caller reads only their last 24
/// hours, or lacks a scope the fact needs. A number only.
/// </param>
public sealed record FlagRuleSummary(
    [property: Required] FlagRule Rule,
    [property: Required] int CannotCheckCount);

/// <summary>The caller's flag rules, oldest first, and the most they may hold.</summary>
public sealed record FlagRulesResponse(
    [property: Required] IReadOnlyList<FlagRuleSummary> Rules,
    [property: Required] int Limit);

/// <summary>The facts a flag rule may name, and the suggested rules the editor offers first.</summary>
public sealed record FlagRuleFactsResponse(
    [property: Required] IReadOnlyList<FlagRulePreset> Presets,
    [property: Required] IReadOnlyList<CustomReportFlagFact> Facts);

/// <summary>
/// The signed-in person's own flag rules: each compares a fact of the people they follow with a value, and a person
/// whose figure meets one is flagged for them on the People page, with the rule's reason.
/// </summary>
/// <remarks>
/// Run by a person in a browser session (<see cref="RequireInteractiveSessionAttribute"/>). A rule belongs to the
/// person, not to any tenant, so these routes are served on every host. Neither the list nor the facts read any
/// person's data: the facts are the reports engine's, and a rule's count of people it cannot be checked for comes from
/// the caller's access alone. The facts need no reports scope.
/// </remarks>
[ApiController]
[Tags("Identity")]
[Route("api/v4/me/flag-rules")]
[Produces("application/json")]
[Authorize]
[RequireInteractiveSession]
[DenyDemoSubject]
[ErrorEnvelope]
public class FlagRulesController(
    IFlagRuleRepository rules,
    IFlagRuleCatalogue catalogue,
    ITenantOverviewService overview) : ControllerBase
{
    internal const string LimitReachedCode = "flag_rule_limit_reached";

    /// <summary>The caller's rules, and for each how many of the people they follow it cannot be checked for.</summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(FlagRulesResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFlagRules(CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var caller = HttpContext.GetAuthContext() ?? AuthContext.Unauthenticated();
        var held = await rules.GetOfSubjectAsync(subjectId, ct);
        var people = held.Count == 0
            ? []
            : await overview.GetGlucoseReadTenantsAsync(subjectId, HttpContext.GetTenantlessTokenScopes(caller), caller.AuthType, ct);
        var counts = catalogue.CannotCheckCounts(held, people, caller.LimitTo24Hours);
        return Ok(new FlagRulesResponse(
            [.. held.Select((rule, i) => new FlagRuleSummary(rule, counts[i]))],
            FlagRuleLimits.PerViewer));
    }

    /// <summary>The suggested rules and every fact a rule may name, each with its unit and, for a range fact, its family.</summary>
    [HttpGet("facts")]
    [RemoteQuery]
    [ProducesResponseType(typeof(FlagRuleFactsResponse), StatusCodes.Status200OK)]
    public IActionResult GetFlagRuleFacts() =>
        Ok(new FlagRuleFactsResponse(catalogue.Presets, catalogue.Facts));

    /// <summary>Adds a rule. Refused with 409 once the caller holds the most they may.</summary>
    [HttpPost]
    [RemoteCommand(Invalidates = ["GetFlagRules"])]
    [ProducesResponseType(typeof(FlagRule), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateFlagRule([FromBody] FlagRuleRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var draft = DraftOf(request);
        if (catalogue.Check(draft) is { Count: > 0 } errors)
            return ValidationProblem(new ValidationProblemDetails(errors.ToDictionary(e => e.Key, e => e.Value)));

        return await rules.CreateAsync(subjectId, draft, ct) is { } rule
            ? StatusCode(StatusCodes.Status201Created, rule)
            : this.ProblemWithCode(
                LimitReachedCode,
                $"You can have at most {FlagRuleLimits.PerViewer} flag rules. Delete one to add another.",
                StatusCodes.Status409Conflict);
    }

    /// <summary>Changes a rule of the caller's; 404 for any other.</summary>
    [HttpPut("{ruleId:guid}")]
    [RemoteCommand(Invalidates = ["GetFlagRules"])]
    [ProducesResponseType(typeof(FlagRule), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateFlagRule(Guid ruleId, [FromBody] FlagRuleRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var draft = DraftOf(request);
        if (catalogue.Check(draft) is { Count: > 0 } errors)
            return ValidationProblem(new ValidationProblemDetails(errors.ToDictionary(e => e.Key, e => e.Value)));

        return await rules.UpdateAsync(subjectId, ruleId, draft, ct) is { } rule ? Ok(rule) : NotFound();
    }

    /// <summary>Deletes a rule of the caller's; 404 for any other.</summary>
    [HttpDelete("{ruleId:guid}")]
    [RemoteCommand(Invalidates = ["GetFlagRules"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteFlagRule(Guid ruleId, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        return await rules.DeleteAsync(subjectId, ruleId, ct) ? NoContent() : NotFound();
    }

    /// <summary>A request <see cref="Validators.V4.FlagRuleRequestValidator"/> has passed, so its comparison and value are set.</summary>
    private static FlagRuleDraft DraftOf(FlagRuleRequest request) => new(
        request.Dataset,
        request.Field,
        request.Days,
        request.Comparison!.Value,
        request.Value!.Value,
        request.Level ?? FlagRuleLimits.DefaultLevel);
}
