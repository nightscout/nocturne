using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Controllers.Admin;

public class TenantControllerEmailConfirmationTests : IDisposable
{
    private const string OnAccount = "member-on-file@example.test";

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly Mock<ITenantService> _tenantService = new();
    private readonly Mock<IAuthAuditService> _audit = new();
    private readonly ISubjectService _subjectService;

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _memberId = Guid.CreateVersion7();
    private readonly Guid _noAddressMemberId = Guid.CreateVersion7();
    private readonly Guid _outsiderId = Guid.CreateVersion7();

    public TenantControllerEmailConfirmationTests()
    {
        var member = new SubjectEntity { Id = _memberId, Name = "Member", IsActive = true };
        member.SetEmail(OnAccount);
        using var seed = _db.CreateContext(_tenantId);
        seed.Subjects.Add(member);
        seed.Subjects.Add(new SubjectEntity { Id = _noAddressMemberId, Name = "No address", IsActive = true });
        var outsider = new SubjectEntity { Id = _outsiderId, Name = "Outsider", IsActive = true };
        outsider.SetEmail("outsider@example.test");
        seed.Subjects.Add(outsider);
        seed.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "acme", DisplayName = "Acme", IsActive = true });
        seed.TenantMembers.AddRange(
            new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = _memberId },
            new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = _noAddressMemberId });
        seed.SaveChanges();

        _subjectService = new SubjectService(
            new SubjectRepository(_db.ContextFactory.AsNocturneFactory(), AccountLinkRoutes.Off),
            _audit.Object,
            Mock.Of<IRecoveryCodeService>(),
            NullLogger<SubjectService>.Instance,
            TimeProvider.System);

        _tenantService
            .Setup(s => s.GetByIdAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantDetailDto(
                _tenantId, "acme", "Acme", true, DateTime.UtcNow,
                [Member(_memberId), Member(_noAddressMemberId)]));
    }

    public void Dispose() => _db.Dispose();

    private static TenantMemberDto Member(Guid subjectId) => new(
        Guid.CreateVersion7(), subjectId, "Member", false, [], null, null, false, null, DateTime.UtcNow, false, null, false, null, true, [], false);

    private TenantController Controller(AuthContext auth) => new(_tenantService.Object, Mock.Of<ITenantRoleService>())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Items = { ["AuthContext"] = auth } },
        },
    };

    private static AuthContext InstanceKey() => new()
    {
        IsAuthenticated = true,
        AuthType = AuthType.InstanceKey,
        IsPlatformAdmin = true,
        CredentialFingerprint = "0123456789abcdef",
    };

    private Task<IActionResult> ConfirmAsync(Guid subjectId, string email, AuthContext? auth = null, Guid? tenantId = null) =>
        Controller(auth ?? InstanceKey()).ConfirmMemberEmail(
            tenantId ?? _tenantId, subjectId, new AdminConfirmEmailRequest(email),
            _subjectService, _audit.Object,
            new TenantMemberService(
                new TenantMembershipRepository(_db.ContextFactory.AsNocturneFactory(), AccountLinkRoutes.Off), TimeProvider.System),
            CancellationToken.None);

    private async Task<SubjectEntity> RowAsync(Guid subjectId)
    {
        await using var db = _db.CreateContext();
        return await db.Subjects.AsNoTracking().SingleAsync(s => s.Id == subjectId);
    }

    private async Task<DateTime?> ConfirmedAtAsync(Guid subjectId) => (await RowAsync(subjectId)).EmailConfirmedAt;

    [Fact]
    public async Task A_caller_who_does_not_own_the_tenant_is_refused_and_confirms_nothing()
    {
        var notAnOwner = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            IsPlatformAdmin = true,
            SubjectId = _outsiderId,
        };

        var result = await ConfirmAsync(_memberId, OnAccount, notAnOwner);

        result.Should().BeOfType<ForbidResult>();
        (await ConfirmedAtAsync(_memberId)).Should().BeNull();
    }

    [Fact]
    public async Task A_subject_who_is_not_a_member_of_the_tenant_answers_404_and_confirms_nothing()
    {
        var result = await ConfirmAsync(_outsiderId, "outsider@example.test");

        result.Should().BeOfType<NotFoundResult>();
        (await ConfirmedAtAsync(_outsiderId)).Should().BeNull();
    }

    [Fact]
    public async Task A_member_whose_access_has_ended_answers_as_a_non_member_and_confirms_nothing()
    {
        await using (var db = _db.CreateContext(_tenantId))
        {
            await db.TenantMembers.Where(m => m.SubjectId == _memberId)
                .ExecuteUpdateAsync(m => m.SetProperty(x => x.AccessEndsAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        var ended = await ConfirmAsync(_memberId, OnAccount);
        var nonMember = await ConfirmAsync(_outsiderId, "outsider@example.test");

        ended.Should().BeOfType<NotFoundResult>();
        nonMember.Should().BeOfType<NotFoundResult>();
        (await ConfirmedAtAsync(_memberId)).Should().BeNull();
        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.EmailConfirmedByOperator, It.IsAny<Guid?>(), It.IsAny<bool>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(),
            It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task A_member_whose_access_ends_later_is_confirmed()
    {
        await using (var db = _db.CreateContext(_tenantId))
        {
            await db.TenantMembers.Where(m => m.SubjectId == _memberId)
                .ExecuteUpdateAsync(m => m.SetProperty(x => x.AccessEndsAt, DateTime.UtcNow.AddDays(1)));
        }

        var result = await ConfirmAsync(_memberId, OnAccount);

        result.Should().BeOfType<OkObjectResult>();
        (await ConfirmedAtAsync(_memberId)).Should().NotBeNull();
    }

    [Fact]
    public async Task An_unknown_tenant_answers_404()
    {
        var result = await ConfirmAsync(_memberId, OnAccount, tenantId: Guid.CreateVersion7());

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task An_address_that_is_not_on_the_account_answers_409_with_the_code_and_never_the_real_address()
    {
        var result = await ConfirmAsync(_memberId, "typo@example.test");

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions.Should().ContainKey("error").WhoseValue.Should().Be("AddressMismatch");
        System.Text.Json.JsonSerializer.Serialize(problem).Should().NotContain(OnAccount);
        (await ConfirmedAtAsync(_memberId)).Should().BeNull();
    }

    [Fact]
    public async Task An_account_with_no_address_answers_409_and_stays_without_one()
    {
        var result = await ConfirmAsync(_noAddressMemberId, "anything@example.test");

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        objectResult.Value.Should().BeOfType<ProblemDetails>()
            .Which.Extensions.Should().ContainKey("error").WhoseValue.Should().Be("AddressMismatch");
        var row = await RowAsync(_noAddressMemberId);
        row.Email.Should().BeNull();
        row.EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_address_on_the_account_is_confirmed_and_the_answer_carries_the_time()
    {
        var result = await ConfirmAsync(_memberId, OnAccount.ToUpperInvariant());

        var body = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<EmailConfirmedDto>().Subject;
        body.ConfirmedAt.Should().Be(await ConfirmedAtAsync(_memberId));
        body.ConfirmedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        (await RowAsync(_memberId)).Email.Should().Be(OnAccount);
    }

    [Fact]
    public async Task Confirming_twice_answers_the_same_and_keeps_the_first_time()
    {
        var first = (await ConfirmAsync(_memberId, OnAccount))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<EmailConfirmedDto>().Subject;

        var second = (await ConfirmAsync(_memberId, OnAccount))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<EmailConfirmedDto>().Subject;

        second.Should().Be(first);
    }

    [Fact]
    public async Task A_confirmation_is_audited_without_the_address()
    {
        await ConfirmAsync(_memberId, OnAccount);

        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.EmailConfirmedByOperator, _memberId, true,
            It.IsAny<string?>(), It.IsAny<string?>(), null, null, null,
            It.Is<AuthAuditActor?>(actor => actor != null), _tenantId));
    }

    [Fact]
    public async Task A_refusal_is_not_audited_as_a_confirmation()
    {
        await ConfirmAsync(_memberId, "typo@example.test");

        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.EmailConfirmedByOperator, It.IsAny<Guid?>(), It.IsAny<bool>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(),
            It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()), Times.Never);
    }
}
