using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Reports;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// <c>/api/v4/me/people-views</c>: the caller's own views, refused before they reach the store when a fact filter names a
/// fact the reports validator refuses, held to the limit and to one name, reordered whole, and listed with whether a
/// view names a group or a team the caller no longer has.
/// </summary>
[Trait("Category", "Unit")]
public class PeopleViewsControllerTests
{
    private readonly Mock<IPeopleViewRepository> _views = new();
    private readonly Mock<IFlagRuleCatalogue> _catalogue = new();
    private readonly Mock<ITeamRepository> _teams = new(MockBehavior.Strict);
    private readonly Mock<ITenantMembershipRepository> _members = new(MockBehavior.Strict);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 28, 9, 0, 0, TimeSpan.Zero));
    private readonly Guid _subjectId = Guid.CreateVersion7();

    private static readonly PeopleViewRequest Request = new("  Night shift ", null, PeopleLens.Review, 30);

    public PeopleViewsControllerTests() =>
        _catalogue.Setup(c => c.CheckFacts(It.IsAny<PeopleQuery>())).Returns(new Dictionary<string, string[]>());

    private static PeopleFilter Filter(PeopleFilterKind kind) => new(kind, null, null, null, null, null, null, null, null, null);

    private PeopleView ViewOf(PeopleQuery query, int position = 0) =>
        new(Guid.CreateVersion7(), $"View {position}", position, query, PeopleLens.Now, null, _time.GetUtcNow().UtcDateTime, _time.GetUtcNow().UtcDateTime);

    [Fact]
    public async Task A_view_is_created_for_the_caller_with_its_name_trimmed_and_an_empty_query_when_none_is_sent()
    {
        var created = ViewOf(PeopleQuery.Empty);
        _views.Setup(v => v.CreateAsync(_subjectId, It.IsAny<PeopleViewDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PeopleViewWrite(PeopleViewChange.Changed, created));

        var result = await Controller().CreatePeopleView(Request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status201Created);
        _views.Verify(v => v.CreateAsync(_subjectId,
            new PeopleViewDraft("Night shift", PeopleQuery.Empty, PeopleLens.Review, 30), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_view_whose_fact_filter_the_catalogue_refuses_is_a_validation_problem_and_is_not_stored()
    {
        _catalogue.Setup(c => c.CheckFacts(It.IsAny<PeopleQuery>()))
            .Returns(new Dictionary<string, string[]> { ["query.filters[0].field"] = ["Send a fact a flag rule can name."] });

        var created = await Controller().CreatePeopleView(Request, CancellationToken.None);
        var changed = await Controller().UpdatePeopleView(Guid.NewGuid(), Request, CancellationToken.None);

        foreach (var result in new[] { created, changed })
        {
            var problem = result.Should().BeAssignableTo<ObjectResult>().Which;
            problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            problem.Value.Should().BeOfType<ValidationProblemDetails>().Which.Errors.Keys.Should().Equal("query.filters[0].field");
        }
        _views.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(PeopleViewChange.LimitReached, PeopleViewsController.LimitReachedCode)]
    [InlineData(PeopleViewChange.NameTaken, PeopleViewsController.NameTakenCode)]
    public async Task A_view_past_the_limit_or_with_a_name_taken_is_refused_with_its_code(PeopleViewChange change, string code)
    {
        _views.Setup(v => v.CreateAsync(_subjectId, It.IsAny<PeopleViewDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PeopleViewWrite(change, null));

        var result = await Controller().CreatePeopleView(Request, CancellationToken.None);

        var problem = result.Should().BeAssignableTo<ObjectResult>().Which;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        ((ProblemDetails)problem.Value!).Extensions["code"].Should().Be(code);
    }

    [Fact]
    public async Task Changing_a_view_to_a_name_taken_is_refused_and_changing_or_deleting_a_view_the_caller_does_not_hold_is_not_found()
    {
        var viewId = Guid.NewGuid();
        _views.SetupSequence(v => v.UpdateAsync(_subjectId, viewId, It.IsAny<PeopleViewDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PeopleViewWrite(PeopleViewChange.NameTaken, null))
            .ReturnsAsync(new PeopleViewWrite(PeopleViewChange.NotFound, null));
        _views.Setup(v => v.DeleteAsync(_subjectId, viewId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var taken = await Controller().UpdatePeopleView(viewId, Request, CancellationToken.None);
        var missing = await Controller().UpdatePeopleView(viewId, Request, CancellationToken.None);
        var deleted = await Controller().DeletePeopleView(viewId, CancellationToken.None);

        taken.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        missing.Should().BeOfType<NotFoundResult>();
        deleted.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_view_the_caller_holds_is_changed_and_deleted()
    {
        var view = ViewOf(PeopleQuery.Empty);
        _views.Setup(v => v.UpdateAsync(_subjectId, view.Id, It.IsAny<PeopleViewDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PeopleViewWrite(PeopleViewChange.Changed, view));
        _views.Setup(v => v.DeleteAsync(_subjectId, view.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        (await Controller().UpdatePeopleView(view.Id, Request, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(view);
        (await Controller().DeletePeopleView(view.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task A_reorder_returns_the_views_in_their_new_order_and_one_that_does_not_name_each_view_once_is_refused()
    {
        var first = ViewOf(PeopleQuery.Empty, 0);
        var second = ViewOf(PeopleQuery.Empty, 1);
        IReadOnlyList<PeopleView> ordered = [second with { Position = 0 }, first with { Position = 1 }];
        _views.Setup(v => v.ReorderAsync(_subjectId, It.Is<IReadOnlyList<Guid>>(o => o.SequenceEqual(new[] { second.Id, first.Id })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ordered);
        _views.Setup(v => v.ReorderAsync(_subjectId, It.Is<IReadOnlyList<Guid>>(o => o.Count == 1), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PeopleView>?)null);

        var reordered = await Controller().ReorderPeopleViews(new([second.Id, first.Id]), CancellationToken.None);
        var refused = await Controller().ReorderPeopleViews(new([first.Id]), CancellationToken.None);

        reordered.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(ordered);
        var problem = refused.Should().BeAssignableTo<ObjectResult>().Which;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        ((ProblemDetails)problem.Value!).Extensions["code"].Should().Be(PeopleViewsController.OrderMismatchCode);
    }

    [Fact]
    public async Task The_list_says_which_views_name_a_group_or_a_team_the_caller_no_longer_has()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var group = Guid.CreateVersion7();
        var team = Guid.CreateVersion7();
        var quiet = Guid.CreateVersion7();
        var plain = ViewOf(new PeopleQuery("ann", [Filter(PeopleFilterKind.SourceDirect)], null), 0);
        var inGroup = ViewOf(new PeopleQuery(null, [Filter(PeopleFilterKind.InGroup) with { GroupId = group }], null), 1);
        var leftGroup = ViewOf(new PeopleQuery(null, [Filter(PeopleFilterKind.InGroup) with { GroupId = Guid.CreateVersion7() }], null), 2);
        var viaTeam = ViewOf(new PeopleQuery(null, [Filter(PeopleFilterKind.SourceTeam) with { TeamId = team }], null), 3);
        var leftTeam = ViewOf(new PeopleQuery(null, [Filter(PeopleFilterKind.SourceTeam) with { TeamId = Guid.CreateVersion7() }], null), 4);
        var quietTeam = ViewOf(new PeopleQuery(null, [Filter(PeopleFilterKind.SourceTeam) with { TeamId = quiet }], null), 5);
        _views.Setup(v => v.GetOfSubjectAsync(_subjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([plain, inGroup, leftGroup, viaTeam, leftTeam, quietTeam]);
        _teams.Setup(t => t.GetGroupsOfStaffAsync(_subjectId, now, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new StaffGroup(group, "Birch", "Camp", false)]);
        _members.Setup(m => m.ListSourcesOfSubjectAsync(_subjectId, now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, MembershipSources>
            {
                [Guid.CreateVersion7()] = new(true, new HashSet<Guid> { team }),
                [Guid.CreateVersion7()] = new(true, new HashSet<Guid>()),
            });

        var response = (await Controller().GetPeopleViews(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PeopleViewsResponse>().Subject;

        response.Limit.Should().Be(PeopleViewLimits.PerViewer);
        response.Views.Select(v => (v.View, v.GroupGone, v.TeamGone)).Should().Equal(
            (plain, false, false), (inGroup, false, false), (leftGroup, true, false), (viaTeam, false, false), (leftTeam, false, true),
            (quietTeam, false, true));
        _teams.Verify(t => t.GetTeamsAndTheirPeopleAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never,
            "a team the caller is still in but follows no one through is gone to the filter, so its membership is not read");
    }

    [Fact]
    public async Task A_list_with_no_group_or_team_filter_reads_neither()
    {
        _views.Setup(v => v.GetOfSubjectAsync(_subjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([ViewOf(PeopleQuery.Empty)]);

        var response = (await Controller().GetPeopleViews(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PeopleViewsResponse>().Subject;

        response.Views.Should().ContainSingle().Which.Should().Match<PeopleViewSummary>(v => !v.GroupGone && !v.TeamGone);
        _teams.VerifyNoOtherCalls();
        _members.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_caller_with_no_subject_is_refused_and_nothing_is_read()
    {
        var controller = Controller(subject: false);

        (await controller.GetPeopleViews(CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        (await controller.CreatePeopleView(Request, CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        (await controller.ReorderPeopleViews(new([]), CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        _views.VerifyNoOtherCalls();
    }

    private PeopleViewsController Controller(bool subject = true)
    {
        var httpContext = new DefaultHttpContext { RequestServices = TestRequestServices.Build(clamped: false) };
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = subject ? _subjectId : null,
        };
        return new PeopleViewsController(_views.Object, _catalogue.Object, _teams.Object, _members.Object, _time)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
