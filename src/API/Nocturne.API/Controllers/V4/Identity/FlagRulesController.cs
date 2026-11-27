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

/// <summary>A flag rule, and how many of the people it applies to it cannot be checked for by the caller.</summary>
/// <param name="CannotCheckCount">
/// How many of the people the caller follows that the rule applies to it cannot be checked for: the caller reads only
/// their last 24 hours, or lacks a scope the fact needs. For a team's rule, of the people the team follows that the
/// caller follows. A number only.
/// </param>
public sealed record FlagRuleSummary(
    [property: Required] FlagRule Rule,
    [property: Required] int CannotCheckCount);

/// <summary>The flag rules of the caller or of a team, oldest first, and the most they may hold.</summary>
public sealed record FlagRulesResponse(
    [property: Required] IReadOnlyList<FlagRuleSummary> Rules,
    [property: Required] int Limit);

/// <summary>The facts a flag rule may name, and the suggested rules the editor offers first.</summary>
public sealed record FlagRuleFactsResponse(
    [property: Required] IReadOnlyList<FlagRulePreset> Presets,
    [property: Required] IReadOnlyList<CustomReportFlagFact> Facts);

/// <summary>
/// The signed-in person's own flag rules, and the rules of their teams: each compares a fact of the people they follow
/// with a value, and a person whose figure meets one is flagged on the People page, with the rule's reason. A person's
/// own rules are theirs alone. A team's rules apply for all its staff to everyone the team follows, and on the wall;
/// its staff read them, and only its managers change them.
/// </summary>
/// <remarks>
/// Run by a person in a browser session (<see cref="RequireInteractiveSessionAttribute"/>). A rule belongs to a person
/// or a team, not to any tenant, so these routes are served on every host. A team's rules are at the team's own path,
/// beside its other routes. A caller who is not a member of the team whose end has not passed gets 404, and so does a
/// staff member who writes, as on every route of a team that only a manager may use. Neither list nor the facts read
/// any person's data: the facts are the reports engine's, and a rule's count of people it cannot be checked for comes
/// from the caller's access alone. The facts need no reports scope.
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
    ITenantOverviewService overview,
    ITeamRepository teams,
    TimeProvider time) : ControllerBase
{
    internal const string LimitReachedCode = "flag_rule_limit_reached";

    private const string TeamRoute = "~/api/v4/me/teams/{teamId:guid}/flag-rules";

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
        return Ok(Summaries(held, people, caller, FlagRuleLimits.PerViewer));
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
        if (Refusal(draft) is { } refusal)
            return refusal;

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
        if (Refusal(draft) is { } refusal)
            return refusal;

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

    /// <summary>
    /// The team's rules, and for each how many of the people the team follows that the caller follows it cannot be
    /// checked for. 404 when the caller is not a member of the team whose end has not passed.
    /// </summary>
    [HttpGet(TeamRoute)]
    [RemoteQuery]
    [ProducesResponseType(typeof(FlagRulesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTeamFlagRules(Guid teamId, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var now = time.GetUtcNow().UtcDateTime;
        if (await rules.GetOfTeamAsync(teamId, subjectId, now, ct) is not { } held)
            return NotFound();

        var caller = HttpContext.GetAuthContext() ?? AuthContext.Unauthenticated();
        IReadOnlyList<GlucoseReadTenant> people = [];
        if (held.Count > 0
            && (await teams.GetTeamsAndTheirPeopleAsync(subjectId, now, ct)).FirstOrDefault(t => t.Id == teamId) is { } team)
        {
            var readable = await overview.GetGlucoseReadTenantsAsync(
                subjectId, HttpContext.GetTenantlessTokenScopes(caller), caller.AuthType, ct);
            people = [.. readable.Where(person => team.People.Contains(person.Tenant.Id))];
        }
        return Ok(Summaries(held, people, caller, FlagRuleLimits.PerTeam));
    }

    /// <summary>Adds a rule to the team. Refused with 409 once the team holds the most it may; 404 for a caller who does not manage it.</summary>
    [HttpPost(TeamRoute)]
    [RemoteCommand(Invalidates = ["GetTeamFlagRules"])]
    [ProducesResponseType(typeof(FlagRule), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateTeamFlagRule(Guid teamId, [FromBody] FlagRuleRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var draft = DraftOf(request);
        if (Refusal(draft) is { } refusal)
            return refusal;

        var write = await rules.CreateForTeamAsync(teamId, subjectId, draft, ct);
        return write.Change switch
        {
            TeamFlagRuleChange.Changed => StatusCode(StatusCodes.Status201Created, write.Rule),
            TeamFlagRuleChange.LimitReached => this.ProblemWithCode(
                LimitReachedCode,
                $"A team can have at most {FlagRuleLimits.PerTeam} flag rules. Delete one to add another.",
                StatusCodes.Status409Conflict),
            _ => NotFound(),
        };
    }

    /// <summary>Changes a rule of the team's; 404 for a caller who does not manage it, and for any other rule.</summary>
    [HttpPut(TeamRoute + "/{ruleId:guid}")]
    [RemoteCommand(Invalidates = ["GetTeamFlagRules"])]
    [ProducesResponseType(typeof(FlagRule), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateTeamFlagRule(
        Guid teamId, Guid ruleId, [FromBody] FlagRuleRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var draft = DraftOf(request);
        if (Refusal(draft) is { } refusal)
            return refusal;

        var write = await rules.UpdateForTeamAsync(teamId, subjectId, ruleId, draft, ct);
        return write.Change == TeamFlagRuleChange.Changed ? Ok(write.Rule) : NotFound();
    }

    /// <summary>Deletes a rule of the team's; 404 for a caller who does not manage it, and for any other rule.</summary>
    [HttpDelete(TeamRoute + "/{ruleId:guid}")]
    [RemoteCommand(Invalidates = ["GetTeamFlagRules"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTeamFlagRule(Guid teamId, Guid ruleId, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        return await rules.DeleteForTeamAsync(teamId, subjectId, ruleId, ct) == TeamFlagRuleChange.Changed
            ? NoContent()
            : NotFound();
    }

    /// <summary>Each of <paramref name="held"/> with how many of <paramref name="people"/> it cannot be checked for by <paramref name="caller"/>.</summary>
    private FlagRulesResponse Summaries(
        IReadOnlyList<FlagRule> held, IReadOnlyList<GlucoseReadTenant> people, AuthContext caller, int limit)
    {
        var counts = catalogue.CannotCheckCounts(held, people, caller.LimitTo24Hours);
        return new FlagRulesResponse([.. held.Select((rule, i) => new FlagRuleSummary(rule, counts[i]))], limit);
    }

    /// <summary>What is wrong with the fact <paramref name="draft"/> names (<see cref="IFlagRuleCatalogue.Check"/>); null when it checks.</summary>
    private IActionResult? Refusal(FlagRuleDraft draft) =>
        catalogue.Check(draft) is { Count: > 0 } errors
            ? ValidationProblem(new ValidationProblemDetails(errors.ToDictionary(e => e.Key, e => e.Value)))
            : null;

    /// <summary>A request <see cref="Validators.V4.FlagRuleRequestValidator"/> has passed, so its comparison and value are set.</summary>
    private static FlagRuleDraft DraftOf(FlagRuleRequest request) => new(
        request.Dataset,
        request.Field,
        request.Days,
        request.Comparison!.Value,
        request.Value!.Value,
        request.Level ?? FlagRuleLimits.DefaultLevel);
}
