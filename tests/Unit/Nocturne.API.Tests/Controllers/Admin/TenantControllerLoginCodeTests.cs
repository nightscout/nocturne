using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Controllers.Admin;

public class TenantControllerLoginCodeTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _tenantContext;
    private readonly NocturneDbContext _auditContext;
    private readonly Mock<ITenantService> _tenantService = new();
    private readonly ILoginCodeService _loginCodeService;
    private readonly TenantController _controller;

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _memberSubjectId = Guid.CreateVersion7();
    private readonly Guid _outsiderSubjectId = Guid.CreateVersion7();
    private readonly Guid _adminSubjectId = Guid.CreateVersion7();

    public TenantControllerLoginCodeTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _tenantContext = _db.CreateContext(_tenantId);

        _tenantContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId, Slug = "acme", DisplayName = "Acme", IsActive = true,
        });
        _tenantContext.Subjects.Add(new SubjectEntity { Id = _memberSubjectId, Name = "Owner", IsActive = true });
        _tenantContext.Subjects.Add(new SubjectEntity { Id = _outsiderSubjectId, Name = "Outsider", IsActive = true });
        _tenantContext.Subjects.Add(new SubjectEntity
        {
            Id = _adminSubjectId, Name = "Platform Admin", IsActive = true, IsPlatformAdmin = true,
        });
        _tenantContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = _memberSubjectId,
        });
        _tenantContext.SaveChanges();

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.InstanceKey,
            IsPlatformAdmin = true,
            CredentialFingerprint = "0123456789abcdef",
        };

        _auditContext = _db.CreateContext();
        var jwtService = new Mock<IJwtService>();
        jwtService.Setup(j => j.GenerateRefreshToken()).Returns(() => Guid.NewGuid().ToString("N"));

        _loginCodeService = new LoginCodeService(
            jwtService.Object,
            new AuthAuditService(
                _auditContext.AsFactory(),
                new HttpContextAccessor { HttpContext = httpContext },
                new AuditContext(),
                new Mock<ILogger<AuthAuditService>>().Object));

        _tenantService
            .Setup(s => s.GetByIdAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TenantDetail(isActive: true));

        _controller = new TenantController(_tenantService.Object, new Mock<ITenantRoleService>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    public void Dispose()
    {
        _auditContext.Dispose();
        _tenantContext.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task IssueLoginCode_ForAMember_StoresOnlyTheHashAgainstTheTenantAndSubject()
    {
        var response = await IssueAsync(_memberSubjectId);

        response.Code.Should().NotBeNullOrWhiteSpace();
        response.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(30));

        var stored = await _tenantContext.LoginCodes.AsNoTracking().SingleAsync();
        stored.CodeHash.Should().Be(HashUtils.Sha256Hex(response.Code));
        stored.CodeHash.Should().NotBe(response.Code);
        stored.TenantId.Should().Be(_tenantId);
        stored.SubjectId.Should().Be(_memberSubjectId);
        stored.ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task IssueLoginCode_ForAnUnknownTenant_ReturnsNotFound()
    {
        var unknownTenantId = Guid.CreateVersion7();
        _tenantService
            .Setup(s => s.GetByIdAsync(unknownTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantDetailDto?)null);

        var result = await InvokeAsync(unknownTenantId, _memberSubjectId);

        result.Should().BeOfType<NotFoundResult>();
        (await _tenantContext.LoginCodes.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IssueLoginCode_ForASubjectWhoIsNotAMember_ReturnsNotFound()
    {
        var result = await InvokeAsync(_tenantId, _outsiderSubjectId);

        result.Should().BeOfType<NotFoundResult>();
        (await _tenantContext.LoginCodes.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IssueLoginCode_OnAnInactiveTenant_IsRefusedAndMintsNothing()
    {
        _tenantService
            .Setup(s => s.GetByIdAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TenantDetail(isActive: false));

        var result = await InvokeAsync(_tenantId, _memberSubjectId);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        (await _tenantContext.LoginCodes.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IssueLoginCode_FilesAnAuditRowNamingTheInstanceKeyAsActor()
    {
        await IssueAsync(_memberSubjectId);

        var row = await _auditContext.AuthAuditLog.AsNoTracking()
            .SingleAsync(a => a.EventType == AuthAuditEventType.LoginCodeIssued);
        row.Success.Should().BeTrue();
        row.TargetSubjectId.Should().Be(_memberSubjectId);
        row.ActorSubjectId.Should().BeNull();
        row.ActorCredential.Should().Be($"{AuthType.InstanceKey}:0123456789abcdef");
        row.TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task IssueLoginCode_DropsConsumedAndExpiredCodesOnTheTenant()
    {
        var consumedId = Guid.CreateVersion7();
        var expiredId = Guid.CreateVersion7();
        _tenantContext.LoginCodes.AddRange(
            new LoginCodeEntity
            {
                Id = consumedId,
                TenantId = _tenantId,
                SubjectId = _memberSubjectId,
                CodeHash = "consumed",
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                ConsumedAt = DateTime.UtcNow,
            },
            new LoginCodeEntity
            {
                Id = expiredId,
                TenantId = _tenantId,
                SubjectId = _memberSubjectId,
                CodeHash = "expired",
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
            });
        await _tenantContext.SaveChangesAsync();

        await IssueAsync(_memberSubjectId);

        var remaining = await _tenantContext.LoginCodes.AsNoTracking().Select(c => c.Id).ToListAsync();
        remaining.Should().NotContain(consumedId).And.NotContain(expiredId);
        remaining.Should().HaveCount(1);
    }

    [Fact]
    public async Task IssueRecoveryCode_MintsALongerLivedCodeThatOnlyRecoveryRedeems()
    {
        var result = await IssueRecoveryCodeAsync();
        var issued = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<LoginCode>().Subject;

        issued.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(24), TimeSpan.FromMinutes(1));
        var stored = await _tenantContext.LoginCodes.AsNoTracking().SingleAsync();
        stored.Purpose.Should().Be(LoginCodePurpose.OperatorRecovery);

        (await _loginCodeService.RedeemAsync(_tenantContext, issued.Code, [LoginCodePurpose.Login], null, null))
            .Should().BeNull("a recovery code is not a sign-in");
        (await _loginCodeService.RedeemAsync(_tenantContext, issued.Code, LoginCodeService.RecoveryPurposes, null, null))
            .Should().Be(new RedeemedLoginCode(_memberSubjectId, LoginCodePurpose.OperatorRecovery));
    }

    [Fact]
    public async Task IssueRecoveryCode_ReplacesTheMembersOutstandingRecoveryCode()
    {
        var first = (await IssueRecoveryCodeAsync()).Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<LoginCode>().Subject;

        await IssueRecoveryCodeAsync();

        (await _loginCodeService.RedeemAsync(_tenantContext, first.Code, LoginCodeService.RecoveryPurposes, null, null))
            .Should().BeNull();
    }

    [Fact]
    public async Task IssueRecoveryCode_ForAMemberWhoCanStillSignIn_IsRefused()
    {
        _tenantContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(), SubjectId = _memberSubjectId, CredentialId = [3], PublicKey = [1],
            CreatedAt = DateTime.UtcNow,
        });
        await _tenantContext.SaveChangesAsync();

        var result = await IssueRecoveryCodeAsync();

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        (await _tenantContext.LoginCodes.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_login_code_does_not_redeem_as_a_recovery_code()
    {
        var issued = await IssueAsync(_memberSubjectId);

        (await _loginCodeService.RedeemAsync(_tenantContext, issued.Code, LoginCodeService.RecoveryPurposes, null, null))
            .Should().BeNull();
        (await _tenantContext.LoginCodes.AsNoTracking().SingleAsync()).ConsumedAt.Should().BeNull();
    }

    private Task<IActionResult> IssueRecoveryCodeAsync()
    {
        var contexts = _db.ContextFactory.AsNocturneFactory();
        return _controller.IssueRecoveryCode(
            _tenantId, _memberSubjectId, contexts, _loginCodeService,
            new MemberRecoveryEligibility(new TenantMemberService(new TenantMembershipRepository(contexts))), CancellationToken.None);
    }

    private TenantDetailDto TenantDetail(bool isActive) => new(
        _tenantId, "acme", "Acme", isActive, DateTime.UtcNow,
        [
            new TenantMemberDto(
                Guid.CreateVersion7(), _memberSubjectId, "Owner", false, [], null, null, false,
                null, DateTime.UtcNow, false),
        ]);

    private async Task<IActionResult> InvokeAsync(Guid tenantId, Guid subjectId) =>
        await _controller.IssueLoginCode(
            tenantId, subjectId, _db.ContextFactory.AsNocturneFactory(), _loginCodeService, CancellationToken.None);

    private async Task<LoginCode> IssueAsync(Guid subjectId)
    {
        var result = await InvokeAsync(_tenantId, subjectId);
        return result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<LoginCode>().Subject;
    }
}
