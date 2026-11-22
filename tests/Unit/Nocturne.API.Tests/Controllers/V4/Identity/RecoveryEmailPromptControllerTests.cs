using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.CoachMarks;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.CoachMarks;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

[Trait("Category", "Unit")]
public class RecoveryEmailPromptControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private readonly Mock<ISubjectRepository> _subjects = new();
    private readonly Mock<ICoachMarkService> _coachMarks = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly Guid _subjectId = Guid.NewGuid();

    private SubjectRecord Subject(bool confirmed = false, bool demo = false, string? email = "a@example.test") => new(
        _subjectId, "Rhys", null, email, confirmed ? Now.UtcDateTime : null, null, true, false, demo, false,
        "approved", null, null, null, null, Now.UtcDateTime, Now.UtcDateTime, null, []);

    private RecoveryEmailPromptController Controller(
        bool deliveryConfigured = true, AuthType authType = AuthType.SessionCookie, Guid? subjectId = null)
    {
        var controller = new RecoveryEmailPromptController(
            _subjects.Object, _coachMarks.Object, AccountLinkRoutes.Of(deliveryConfigured), _time)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true, AuthType = authType, SubjectId = subjectId ?? _subjectId,
        };
        return controller;
    }

    private void Arrange(SubjectRecord? subject, CoachMarkState? mark)
    {
        _subjects.Setup(s => s.GetAsync(_subjectId, It.IsAny<CancellationToken>())).ReturnsAsync(subject);
        _coachMarks.Setup(c => c.GetAsync(CoachMarkKeys.RecoveryEmailPrompt, It.IsAny<CancellationToken>())).ReturnsAsync(mark);
    }

    private static CoachMarkState Dismissed(DateTimeOffset at) =>
        new() { MarkKey = CoachMarkKeys.RecoveryEmailPrompt, Status = "dismissed", CompletedAt = at.UtcDateTime };

    private static async Task<bool> ShowAsync(RecoveryEmailPromptController controller) =>
        (await controller.GetRecoveryEmailPrompt(default)).Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<RecoveryEmailPromptResponse>().Subject.Show;

    [Fact]
    public async Task ShowsToAMemberWithNoConfirmedAddressWhoHasNotDismissedIt()
    {
        Arrange(Subject(), mark: null);

        (await ShowAsync(Controller())).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(29, false)]
    [InlineData(31, true)]
    [InlineData(400, true)]
    public async Task ADismissalHidesItForThirtyDaysAndThenItReturns(int daysAgo, bool expected)
    {
        Arrange(Subject(), Dismissed(Now.AddDays(-daysAgo)));

        (await ShowAsync(Controller())).Should().Be(expected);
    }

    [Fact]
    public async Task ADismissalThatIsAgainMadeAfterItReturnedHidesItAgain()
    {
        Arrange(Subject(), Dismissed(Now.AddDays(-45)));
        (await ShowAsync(Controller())).Should().BeTrue();

        Arrange(Subject(), Dismissed(Now));

        (await ShowAsync(Controller())).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(31)]
    public async Task NeverShowsOnceAnAddressIsConfirmed(int? dismissedDaysAgo)
    {
        Arrange(Subject(confirmed: true), dismissedDaysAgo is { } days ? Dismissed(Now.AddDays(-days)) : null);

        (await ShowAsync(Controller())).Should().BeFalse();
    }

    [Fact]
    public async Task AnUnconfirmedAddressIsStillRemindedAboutAsOneToConfirm()
    {
        Arrange(Subject(confirmed: false), mark: null);

        var response = (await Controller().GetRecoveryEmailPrompt(default)).Result
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<RecoveryEmailPromptResponse>().Subject;

        response.Show.Should().BeTrue();
        response.HasAddress.Should().BeTrue();
    }

    [Fact]
    public async Task NoAddressIsRemindedAboutAsOneToAdd()
    {
        Arrange(Subject(email: null), mark: null);

        var response = (await Controller().GetRecoveryEmailPrompt(default)).Result
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<RecoveryEmailPromptResponse>().Subject;

        response.Show.Should().BeTrue();
        response.HasAddress.Should().BeFalse();
    }

    [Fact]
    public async Task NeverShowsWithNoDeliveryRoute()
    {
        Arrange(Subject(), mark: null);

        (await ShowAsync(Controller(deliveryConfigured: false))).Should().BeFalse();
        _subjects.Verify(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NeverShowsForTheDemoSubject()
    {
        Arrange(Subject(demo: true), mark: null);

        (await ShowAsync(Controller())).Should().BeFalse();
    }

    [Theory]
    [InlineData(AuthType.PlatformAccess)]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.InstanceKey)]
    public async Task NeverShowsForACredentialThatIsNotTheAccount(AuthType authType)
    {
        Arrange(Subject(), mark: null);

        (await ShowAsync(Controller(authType: authType))).Should().BeFalse();
        _subjects.Verify(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NeverShowsForACallerWithoutASubject()
    {
        var controller = Controller();
        controller.HttpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };

        (await ShowAsync(controller)).Should().BeFalse();
    }
}
