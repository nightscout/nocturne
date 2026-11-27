using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Reports;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>A view as it is created or changed.</summary>
/// <param name="Name">Required; unique among the caller's views, ignoring case and the spaces around it.</param>
/// <param name="Query">The slice the view keeps; everyone when left out.</param>
/// <param name="Lens">Required. Nullable so a body without it is refused, not read as the first lens.</param>
/// <param name="ReviewDays">For the Review lens, the period it opens on: 14, 30 or 90 days. Left out for the Now lens.</param>
public sealed record PeopleViewRequest(
    [property: Required] string Name,
    PeopleQuery? Query,
    [property: Required] PeopleLens? Lens,
    int? ReviewDays);

/// <summary>The caller's views, each named once, in the order they are to take.</summary>
public sealed record PeopleViewOrderRequest([property: Required] IReadOnlyList<Guid> ViewIds);

/// <summary>A view, and whether a filter of it names what the caller no longer has.</summary>
/// <param name="GroupGone">An in-group filter of the view names a group the caller is no longer staff of; it keeps no one.</param>
/// <param name="TeamGone">
/// A source filter of the view names a team the caller follows no one through: no membership of theirs holds a reason in
/// force from that team. It keeps no one.
/// </param>
public sealed record PeopleViewSummary(
    [property: Required] PeopleView View,
    [property: Required] bool GroupGone,
    [property: Required] bool TeamGone);

/// <summary>The caller's views, in their order, and the most they may hold.</summary>
public sealed record PeopleViewsResponse(
    [property: Required] IReadOnlyList<PeopleViewSummary> Views,
    [property: Required] int Limit);

/// <summary>
/// The signed-in person's saved views of the People page: each a named query, a lens and, for the Review lens, a
/// period. A view is its owner's alone.
/// </summary>
/// <remarks>
/// Run by a person in a browser session (<see cref="RequireInteractiveSessionAttribute"/>), with the gates of the
/// caller's own flag rules (<see cref="FlagRulesController"/>). A view belongs to a person, not to any tenant, so these
/// routes are served on every host. Nothing here reads any person's data: the list reads only the caller's groups and
/// the teams of their membership reasons to say which views name one they no longer have.
/// </remarks>
[ApiController]
[Tags("Identity")]
[Route("api/v4/me/people-views")]
[Produces("application/json")]
[Authorize]
[RequireInteractiveSession]
[DenyDemoSubject]
[ErrorEnvelope]
public class PeopleViewsController(
    IPeopleViewRepository views,
    IFlagRuleCatalogue catalogue,
    ITeamRepository teams,
    ITenantMembershipRepository members,
    TimeProvider time) : ControllerBase
{
    internal const string LimitReachedCode = "people_view_limit_reached";

    internal const string NameTakenCode = "people_view_name_taken";

    internal const string OrderMismatchCode = "people_view_order_mismatch";

    /// <summary>The caller's views in their order, each saying whether it names a group or a team the caller no longer has.</summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(PeopleViewsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPeopleViews(CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var held = await views.GetOfSubjectAsync(subjectId, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var namesGroup = held.Any(v => Names(v, PeopleFilterKind.InGroup));
        var namesTeam = held.Any(v => Names(v, PeopleFilterKind.SourceTeam));
        IReadOnlySet<Guid> groups = namesGroup
            ? (await teams.GetGroupsOfStaffAsync(subjectId, now, ct)).Select(g => g.Id).ToHashSet()
            : new HashSet<Guid>();
        IReadOnlySet<Guid> followedThrough = namesTeam
            ? (await members.ListSourcesOfSubjectAsync(subjectId, now, ct)).Values.SelectMany(s => s.Teams).ToHashSet()
            : new HashSet<Guid>();

        return Ok(new PeopleViewsResponse(
            [.. held.Select(view => new PeopleViewSummary(
                view,
                view.Query.AllFilters.Any(f => f.Kind == PeopleFilterKind.InGroup
                    && (f.GroupId is not { } group || !groups.Contains(group))),
                view.Query.AllFilters.Any(f => f.Kind == PeopleFilterKind.SourceTeam
                    && (f.TeamId is not { } team || !followedThrough.Contains(team)))))],
            PeopleViewLimits.PerViewer));
    }

    /// <summary>
    /// Adds a view after the caller's last. Refused with 409 once the caller holds the most they may, or when another of
    /// their views has the name.
    /// </summary>
    [HttpPost]
    [RemoteCommand(Invalidates = ["GetPeopleViews"])]
    [ProducesResponseType(typeof(PeopleView), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreatePeopleView([FromBody] PeopleViewRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var draft = DraftOf(request);
        if (Refusal(draft) is { } refusal)
            return refusal;

        var write = await views.CreateAsync(subjectId, draft, ct);
        return write.Change == PeopleViewChange.Changed
            ? StatusCode(StatusCodes.Status201Created, write.View)
            : Refused(write.Change);
    }

    /// <summary>Changes a view of the caller's; 404 for any other, and 409 when another of their views has the name.</summary>
    [HttpPut("{viewId:guid}")]
    [RemoteCommand(Invalidates = ["GetPeopleViews"])]
    [ProducesResponseType(typeof(PeopleView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdatePeopleView(Guid viewId, [FromBody] PeopleViewRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var draft = DraftOf(request);
        if (Refusal(draft) is { } refusal)
            return refusal;

        var write = await views.UpdateAsync(subjectId, viewId, draft, ct);
        return write.Change == PeopleViewChange.Changed ? Ok(write.View) : Refused(write.Change);
    }

    /// <summary>Deletes a view of the caller's; 404 for any other.</summary>
    [HttpDelete("{viewId:guid}")]
    [RemoteCommand(Invalidates = ["GetPeopleViews"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePeopleView(Guid viewId, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        return await views.DeleteAsync(subjectId, viewId, ct) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Puts the caller's views in the order sent and returns them so. Refused with 409, and nothing changed, unless the
    /// order names each of their views exactly once.
    /// </summary>
    [HttpPost("reorder")]
    [RemoteCommand(Invalidates = ["GetPeopleViews"])]
    [ProducesResponseType(typeof(IReadOnlyList<PeopleView>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReorderPeopleViews([FromBody] PeopleViewOrderRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        return await views.ReorderAsync(subjectId, request.ViewIds, ct) is { } ordered
            ? Ok(ordered)
            : Refused(PeopleViewChange.OrderMismatch);
    }

    private static bool Names(PeopleView view, PeopleFilterKind kind) => view.Query.AllFilters.Any(f => f.Kind == kind);

    private IActionResult Refused(PeopleViewChange change) => change switch
    {
        PeopleViewChange.LimitReached => this.ProblemWithCode(
            LimitReachedCode,
            $"You can have at most {PeopleViewLimits.PerViewer} views. Delete one to add another.",
            StatusCodes.Status409Conflict),
        PeopleViewChange.NameTaken => this.ProblemWithCode(
            NameTakenCode,
            "You have a view with this name. Use a different name.",
            StatusCodes.Status409Conflict),
        PeopleViewChange.OrderMismatch => this.ProblemWithCode(
            OrderMismatchCode,
            "Send each of your views one time.",
            StatusCodes.Status409Conflict),
        _ => NotFound(),
    };

    /// <summary>What is wrong with the facts the draft's fact filters name (<see cref="IFlagRuleCatalogue.CheckFacts"/>); null when they check.</summary>
    private IActionResult? Refusal(PeopleViewDraft draft) =>
        catalogue.CheckFacts(draft.Query) is { Count: > 0 } errors
            ? ValidationProblem(new ValidationProblemDetails(errors.ToDictionary(e => e.Key, e => e.Value)))
            : null;

    /// <summary>A request <see cref="Validators.V4.PeopleViewRequestValidator"/> has passed, so its lens is set and its name is not blank.</summary>
    private static PeopleViewDraft DraftOf(PeopleViewRequest request) => new(
        request.Name.Trim(),
        request.Query ?? PeopleQuery.Empty,
        request.Lens!.Value,
        request.ReviewDays);
}
