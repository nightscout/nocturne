using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.CoachMarks;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.CoachMarks;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class BackupSignInPromptControllerTests
{
    private readonly Mock<ISubjectService> _subjectService = new();
    private readonly Mock<ICoachMarkService> _coachMarkService = new();
    private readonly BackupSignInPromptController _controller;
    private readonly Guid _subjectId = Guid.NewGuid();

    public BackupSignInPromptControllerTests()
    {
        _controller = new BackupSignInPromptController(_subjectService.Object, _coachMarkService.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        _controller.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = _subjectId,
        };
    }

    private void HasSingleSignInMethod(bool single) =>
        _subjectService.Setup(s => s.HasSingleSignInMethodAsync(_subjectId)).ReturnsAsync(single);

    private void MarkIs(string? status) =>
        _coachMarkService
            .Setup(s => s.GetAsync(CoachMarkKeys.BackupSignInPrompt, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status is null ? null : new CoachMarkState { MarkKey = CoachMarkKeys.BackupSignInPrompt, Status = status });

    private async Task<bool> Show() =>
        (await _controller.GetBackupSignInPrompt(CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<BackupSignInPromptResponse>().Subject.Show;

    [Theory]
    [InlineData(null, true)]
    [InlineData("seen", true)]
    [InlineData("dismissed", false)]
    [Trait("Category", "Unit")]
    public async Task ShowsOnASingleMethodUnlessItsMarkIsDismissed(string? status, bool expected)
    {
        HasSingleSignInMethod(true);
        MarkIs(status);

        (await Show()).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DoesNotShowWhenTheAccountHasAnotherWayIn()
    {
        HasSingleSignInMethod(false);
        MarkIs(null);

        (await Show()).Should().BeFalse();
        _coachMarkService.Verify(
            s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(AuthType.PlatformAccess)]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.InstanceKey)]
    [Trait("Category", "Unit")]
    public async Task DoesNotShowForACredentialThatIsNotTheAccount(AuthType authType)
    {
        _controller.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = authType,
            SubjectId = _subjectId,
        };
        HasSingleSignInMethod(true);
        MarkIs(null);

        (await Show()).Should().BeFalse();
        _subjectService.Verify(s => s.HasSingleSignInMethodAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DoesNotShowForACallerWithoutASubject()
    {
        _controller.HttpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };

        (await Show()).Should().BeFalse();
        _subjectService.Verify(s => s.HasSingleSignInMethodAsync(It.IsAny<Guid>()), Times.Never);
    }
}
