using Nocturne.API.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>A confirmation link confirms the address it was sent to, and nothing else.</summary>
[Trait("Category", "Unit")]
public class EmailConfirmationTests : IDisposable
{
    private sealed class SharedSqliteFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    private const string Address = "a@example.test";

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly TenantContext _tenant;
    private readonly EmailRecoveryTests.RecordingDelivery _delivery = new();
    private readonly LoginCodeService _loginCodes;
    private readonly INocturneDbContextFactory _contexts;

    public EmailConfirmationTests()
    {
        _tenant = new TenantContext(_tenantId, "alpha", "Alpha household", true, false);
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.GenerateRefreshToken()).Returns(() => Guid.NewGuid().ToString("N"));
        _loginCodes = new LoginCodeService(jwt.Object, new Mock<IAuthAuditService>().Object, TimeProvider.System);
        _contexts = new SharedSqliteFactory(_db.Options).AsNocturneFactory(_tenantId);

        using var seed = _db.CreateContext(Guid.Empty);
        seed.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "alpha", DisplayName = "Alpha household" });
        seed.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private EmailConfirmationController Controller(Guid? signedIn = null, string sessionId = LiveSession.Id)
    {
        var http = new DefaultHttpContext { RequestServices = TestRequestServices.Build(clamped: false) };
        http.SetTenantContext(_tenant);
        if (signedIn is { } subjectId)
        {
            http.Items["AuthContext"] = new AuthContext
            {
                IsAuthenticated = true, AuthType = AuthType.SessionCookie, SubjectId = subjectId, TenantId = _tenantId,
                SessionId = sessionId,
            };
        }

        return new EmailConfirmationController(new SubjectRepository(_contexts, AccountLinkRoutes.Off), _contexts, _loginCodes)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private AccountLinkService Links() => new(
        _contexts, _loginCodes, new SubjectRepository(_contexts, AccountLinkRoutes.Off), _delivery,
        Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example" }), TimeProvider.System);

    private async Task<Guid> SeedMemberAsync(string? email = Address, bool confirmed = false)
    {
        var id = Guid.CreateVersion7();
        await using var db = _db.CreateContext(Guid.Empty);
        var subject = new SubjectEntity { Id = id, Name = "Rhys", IsActive = true };
        subject.SetEmail(email);
        if (confirmed)
            subject.ConfirmEmail(email, DateTime.UtcNow);
        db.Subjects.Add(subject);
        db.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = id });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<SubjectEntity> SubjectAsync(Guid id)
    {
        await using var db = _db.CreateContext(Guid.Empty);
        return await db.Subjects.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private static void ShouldBeProblem(IActionResult result, int status, string code)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(status);
        ((ProblemDetails)problem.Value!).Extensions["code"].Should().Be(code);
    }

    private async Task<string> SentCodeAsync(Guid subjectId)
    {
        (await Controller(subjectId).Send(Links(), default)).Should().BeOfType<NoContentResult>();
        var link = _delivery.Sent.Last().Link;
        return Uri.UnescapeDataString(link[(link.IndexOf("code=", StringComparison.Ordinal) + 5)..]);
    }

    [Fact]
    public async Task Send_DeliversAConfirmationLinkOnTheSitesHostForTheCurrentAddress()
    {
        var memberId = await SeedMemberAsync();

        var result = await Controller(memberId).Send(Links(), default);

        result.Should().BeOfType<NoContentResult>();
        var message = _delivery.Sent.Should().ContainSingle().Subject;
        message.Kind.Should().Be(AccountLinkKind.EmailConfirmation);
        message.Email.Should().Be(Address);
        message.Link.Should().StartWith("https://alpha.nocturne.example/auth/confirm-email?code=");
        message.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(24), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Send_FromAnEndedSession_IsChallengedAndSendsNoLink()
    {
        var memberId = await SeedMemberAsync();

        var result = await Controller(memberId, sessionId: "ended-session").Send(Links(), default);

        result.Should().BeOfType<ChallengeResult>();
        _delivery.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_WithNoDeliveryRoute_Is404()
    {
        var memberId = await SeedMemberAsync();
        _delivery.IsConfigured = false;

        ShouldBeProblem(await Controller(memberId).Send(Links(), default), 404, AccountLinkProblemCodes.Unavailable);
    }

    [Fact]
    public async Task Send_ForAnAccountWithNoAddress_Is409()
    {
        var memberId = await SeedMemberAsync(email: null);

        ShouldBeProblem(await Controller(memberId).Send(Links(), default), 409, AccountLinkProblemCodes.NoAddress);
    }

    [Fact]
    public async Task Send_ForAnAlreadyConfirmedAddress_Is409()
    {
        var memberId = await SeedMemberAsync(confirmed: true);

        ShouldBeProblem(await Controller(memberId).Send(Links(), default), 409, AccountLinkProblemCodes.AlreadyConfirmed);
    }

    [Fact]
    public async Task Send_AgainWithinTwoMinutes_IsThrottledAndSendsNothing()
    {
        var memberId = await SeedMemberAsync();
        await Controller(memberId).Send(Links(), default);

        ShouldBeProblem(await Controller(memberId).Send(Links(), default), 429, AccountLinkProblemCodes.Throttled);
        _delivery.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Redeem_ConfirmsTheAddressAndSaysNothingMore()
    {
        var memberId = await SeedMemberAsync();
        var code = await SentCodeAsync(memberId);

        var controller = Controller();
        var result = await controller.Redeem(new EmailConfirmationRedeemRequest { Code = code }, default);

        result.Should().BeOfType<NoContentResult>();
        (await SubjectAsync(memberId)).EmailConfirmedAt.Should().NotBeNull();
        controller.HttpContext.Response.Headers.SetCookie.Should().BeEmpty("confirming an address starts no session");
    }

    [Fact]
    public async Task Redeem_AfterTheAddressChanged_ConfirmsNothing()
    {
        var memberId = await SeedMemberAsync();
        var code = await SentCodeAsync(memberId);
        await using (var db = _db.CreateContext(Guid.Empty))
        {
            (await db.Subjects.SingleAsync(s => s.Id == memberId)).SetEmail("b@example.test");
            await db.SaveChangesAsync();
        }

        var result = await Controller().Redeem(new EmailConfirmationRedeemRequest { Code = code }, default);

        ShouldBeProblem(result, 400, AccountLinkProblemCodes.InvalidLink);
        (await SubjectAsync(memberId)).EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task Redeem_Twice_IsRefusedTheSecondTime()
    {
        var memberId = await SeedMemberAsync();
        var code = await SentCodeAsync(memberId);
        await Controller().Redeem(new EmailConfirmationRedeemRequest { Code = code }, default);

        ShouldBeProblem(
            await Controller().Redeem(new EmailConfirmationRedeemRequest { Code = code }, default),
            400, AccountLinkProblemCodes.InvalidLink);
    }

    [Theory]
    [InlineData(LoginCodePurpose.EmailRecovery)]
    [InlineData(LoginCodePurpose.RecoverySession)]
    [InlineData(LoginCodePurpose.Recovery)]
    public async Task Redeem_ACodeOfAnotherPurpose_ConfirmsNothingAndLeavesItUnspent(LoginCodePurpose purpose)
    {
        var memberId = await SeedMemberAsync();
        string code;
        await using (var db = _db.CreateContext(_tenantId))
        {
            code = await _loginCodes.IssueAnyAsync(db, memberId, purpose);
        }

        var result = await Controller().Redeem(new EmailConfirmationRedeemRequest { Code = code }, default);

        ShouldBeProblem(result, 400, AccountLinkProblemCodes.InvalidLink);
        (await SubjectAsync(memberId)).EmailConfirmedAt.Should().BeNull();
        await using var check = _db.CreateContext(_tenantId);
        (await _loginCodes.FindLiveAsync(check, code, [purpose])).Should().NotBeNull();
    }
}
