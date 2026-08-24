using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Tests the channel a refused setup travels on: <c>detail</c> carries the
/// <see cref="TotpSetupFailure"/> value, because the generated remote wrapper forwards that field
/// and drops the rest of the body, and the web app owns the wording.
/// </summary>
public class TotpControllerSetupFailureTests
{
    private readonly Mock<ITotpService> _totpService = new();
    private readonly Mock<ISessionService> _sessionService = new();
    private readonly Mock<ISubjectService> _subjectService = new();
    private readonly Mock<IAuthAuditService> _auditService = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly Mock<ITenantMemberService> _tenantMemberService = new();

    private TotpController CreateController()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Guid.CreateVersion7(),
        };

        return new TotpController(
            _totpService.Object,
            _sessionService.Object,
            _subjectService.Object,
            _auditService.Object,
            _tenantAccessor.Object,
            _tenantMemberService.Object,
            Options.Create(new OidcOptions()),
            NullLogger<TotpController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    [Theory]
    [InlineData(TotpSetupFailure.InvalidCode)]
    [InlineData(TotpSetupFailure.ChallengeUnreadable)]
    [InlineData(TotpSetupFailure.ChallengeExpired)]
    [Trait("Category", "Unit")]
    public async Task VerifySetup_WhenRefused_AnswersWithTheFailureValue(TotpSetupFailure failure)
    {
        _totpService
            .Setup(s => s.CompleteSetupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new TotpSetupException(failure));

        var controller = CreateController();
        var result = await controller.VerifySetup(new TotpVerifySetupRequest
        {
            Code = "123456",
            Label = "Label",
            ChallengeToken = "token",
        });

        var problem = result.Result.Should().BeOfType<ObjectResult>()
            .Which.Value.Should().BeOfType<ProblemDetails>().Subject;

        problem.Status.Should().Be(400);
        problem.Detail.Should().Be(failure.ToString(),
            "the web app matches this against the generated TotpSetupFailure enum");
    }

    /// <summary>
    /// The exception message is a log line, not copy, so it must never be what the caller reads —
    /// which is the whole reason the failure is carried as a value.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifySetup_WhenRefused_DoesNotLeakTheExceptionMessage()
    {
        var thrown = new TotpSetupException(TotpSetupFailure.ChallengeExpired);
        _totpService
            .Setup(s => s.CompleteSetupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(thrown);

        var controller = CreateController();
        var result = await controller.VerifySetup(new TotpVerifySetupRequest
        {
            Code = "123456",
            Label = "Label",
            ChallengeToken = "token",
        });

        var problem = (ProblemDetails)((ObjectResult)result.Result!).Value!;
        problem.Detail.Should().NotBe(thrown.Message);
    }
}
