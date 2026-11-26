using Nocturne.API.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Recovery by email: who is sent a link, what the link carries, how often, and when the link
/// starts a recovery session.
/// </summary>
[Trait("Category", "Unit")]
public class EmailRecoveryTests : IDisposable
{
    private sealed class SharedSqliteFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    /// <summary>Records what it is asked to deliver, after an optional gate opens.</summary>
    internal sealed class RecordingDelivery : IAccountLinkDelivery
    {
        public bool IsConfigured { get; set; } = true;
        public bool Succeeds { get; set; } = true;
        public TaskCompletionSource? Gate { get; set; }
        public List<AccountLinkMessage> Sent { get; } = [];

        public async Task<bool> DeliverAsync(AccountLinkMessage message, CancellationToken ct)
        {
            if (Gate is { } gate)
                await gate.Task;
            Sent.Add(message);
            return Succeeds;
        }
    }

    private const string Address = "a@example.test";

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _otherTenantId = Guid.CreateVersion7();
    private readonly TenantContext _tenant;
    private readonly RecordingDelivery _delivery = new();
    private readonly LoginCodeService _loginCodes;

    public EmailRecoveryTests()
    {
        _tenant = new TenantContext(_tenantId, "alpha", "Alpha household", true, false);
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.GenerateRefreshToken()).Returns(() => Guid.NewGuid().ToString("N"));
        _loginCodes = new LoginCodeService(jwt.Object, new Mock<IAuthAuditService>().Object, TimeProvider.System);

        using var seed = _db.CreateContext(Guid.Empty);
        seed.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "alpha", DisplayName = "Alpha household" });
        seed.Tenants.Add(new TenantEntity { Id = _otherTenantId, Slug = "beta", DisplayName = "Beta" });
        seed.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private INocturneDbContextFactory Contexts(Guid tenantId) =>
        new SharedSqliteFactory(_db.Options).AsNocturneFactory(tenantId);

    private AccountLinkService Links(Guid? tenantId = null) => new(
        Contexts(tenantId ?? _tenantId), _loginCodes, new SubjectRepository(Contexts(tenantId ?? _tenantId), AccountLinkRoutes.Off), _delivery,
        Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example" }), TimeProvider.System);

    private async Task<Guid> SeedMemberAsync(
        string name,
        string? email = Address,
        bool confirmed = true,
        bool active = true,
        bool system = false,
        bool demo = false,
        Guid? tenantId = null)
    {
        var id = Guid.CreateVersion7();
        await using var db = _db.CreateContext(Guid.Empty);
        var subject = new SubjectEntity
        {
            Id = id,
            Name = name,
            Username = name.ToLowerInvariant(),
            IsActive = active,
            IsSystemSubject = system,
            IsDemoSubject = demo,
        };
        subject.SetEmail(email);
        if (confirmed)
            subject.ConfirmEmail(email, DateTime.UtcNow);
        db.Subjects.Add(subject);
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId ?? _tenantId,
            SubjectId = id,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private Task RequestAsync(string email = Address) =>
        Links().SendRecoveryLinksAsync(_tenant, email, new AuthAuditActor(null, "unknown"), null, null, default);

    private async Task<List<LoginCodeEntity>> CodesAsync(Guid? tenantId = null)
    {
        await using var db = _db.CreateContext(tenantId ?? _tenantId);
        return await db.LoginCodes.AsNoTracking().ToListAsync();
    }

    private static string CodeIn(AccountLinkMessage message) =>
        Uri.UnescapeDataString(message.Link[(message.Link.IndexOf("code=", StringComparison.Ordinal) + 5)..]);

    #region Who is sent a link

    [Fact]
    public async Task AKnownConfirmedAddress_IsSentOneRecoveryLinkBoundToIt()
    {
        var memberId = await SeedMemberAsync("Rhys");

        await RequestAsync("  A@Example.TEST ");

        var message = _delivery.Sent.Should().ContainSingle().Subject;
        message.Kind.Should().Be(AccountLinkKind.Recovery);
        message.Email.Should().Be(Address);
        message.Link.Should().StartWith("https://alpha.nocturne.example/auth/recovery?code=");
        message.SiteSlug.Should().Be("alpha");
        message.SiteDisplayName.Should().Be("Alpha household");
        message.AccountDisplayName.Should().Be("Rhys");
        message.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(30), TimeSpan.FromMinutes(1));

        await using var db = _db.CreateContext(_tenantId);
        var stored = await db.LoginCodes.AsNoTracking().SingleAsync(c => c.Purpose == LoginCodePurpose.EmailRecovery);
        (await _loginCodes.FindLiveAsync(db, CodeIn(message), [LoginCodePurpose.EmailRecovery]))
            .Should().Be(new RedeemedLoginCode(memberId, LoginCodePurpose.EmailRecovery, Address) { CodeId = stored.Id });
    }

    [Fact]
    public async Task AnUnconfirmedAddress_IsSentNothing()
    {
        await SeedMemberAsync("Rhys", confirmed: false);

        await RequestAsync();

        _delivery.Sent.Should().BeEmpty();
        (await CodesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownAddress_IsSentNothing()
    {
        await SeedMemberAsync("Rhys");

        await RequestAsync("b@example.test");

        _delivery.Sent.Should().BeEmpty();
        (await CodesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAddressDifferingByAKelvinSign_IsNotTheMembersAddress()
    {
        await SeedMemberAsync("Kate", email: "kate@example.test");

        await RequestAsync("Kate@example.test");

        _delivery.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task AnInactiveMember_IsSentNothing()
    {
        await SeedMemberAsync("Rhys", active: false);

        await RequestAsync();

        _delivery.Sent.Should().BeEmpty();
        (await CodesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AMemberOfAnotherSiteOnly_IsSentNothing()
    {
        await SeedMemberAsync("Rhys", tenantId: _otherTenantId);

        await RequestAsync();

        _delivery.Sent.Should().BeEmpty();
        (await CodesAsync(_otherTenantId)).Should().BeEmpty();
    }

    [Fact]
    public async Task ASystemSubject_IsSentNothing()
    {
        await SeedMemberAsync("System", system: true);

        await RequestAsync();

        _delivery.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ADemoSubject_IsSentNothing()
    {
        await SeedMemberAsync("Visitor", demo: true);

        await RequestAsync();

        _delivery.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task TwoMembersSharingTheAddress_AreEachSentTheirOwnLink()
    {
        var first = await SeedMemberAsync("Parent");
        var second = await SeedMemberAsync("Child");

        await RequestAsync();

        _delivery.Sent.Select(m => m.AccountDisplayName).Should().BeEquivalentTo(["Parent", "Child"]);
        _delivery.Sent.Select(m => m.Link).Should().OnlyHaveUniqueItems();
        (await CodesAsync()).Select(c => c.SubjectId).Should().BeEquivalentTo([first, second]);
    }

    [Fact]
    public async Task AFailedDelivery_StillLeavesTheCodeIssued()
    {
        await SeedMemberAsync("Rhys");
        _delivery.Succeeds = false;

        await RequestAsync();

        _delivery.Sent.Should().ContainSingle();
        (await CodesAsync()).Should().ContainSingle(c => c.Purpose == LoginCodePurpose.EmailRecovery);
    }

    [Fact]
    public async Task WithNoDeliveryRoute_NothingIsIssuedOrSent()
    {
        await SeedMemberAsync("Rhys");
        _delivery.IsConfigured = false;

        await RequestAsync();

        _delivery.Sent.Should().BeEmpty();
        (await CodesAsync()).Should().BeEmpty();
    }

    #endregion

    #region How often

    private async Task AgeCodesAsync(TimeSpan by)
    {
        await using var db = _db.CreateContext(_tenantId);
        foreach (var code in await db.LoginCodes.ToListAsync())
            code.CreatedAt -= by;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ASecondRequestWithinTwoMinutes_SendsNothing()
    {
        await SeedMemberAsync("Rhys");

        await RequestAsync();
        await RequestAsync();

        _delivery.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task TheSixthRequestInADay_SendsNothing_EvenAfterSpentCodesArePruned()
    {
        var memberId = await SeedMemberAsync("Rhys");
        for (var i = 0; i < LoginCodeService.EmailsPerWindow; i++)
        {
            await RequestAsync();
            await AgeCodesAsync(TimeSpan.FromMinutes(40));
        }

        // Each code is now expired and spent or superseded, which is what issuing prunes.
        await using (var db = _db.CreateContext(_tenantId))
            await _loginCodes.IssueAsync(db, memberId, LoginCodePurpose.Login, null, null, new AuthAuditActor(null, "test"));
        await RequestAsync();

        _delivery.Sent.Should().HaveCount(LoginCodeService.EmailsPerWindow);
    }

    [Fact]
    public async Task ARequestADayLater_IsSentAgain()
    {
        await SeedMemberAsync("Rhys");
        for (var i = 0; i < LoginCodeService.EmailsPerWindow; i++)
        {
            await RequestAsync();
            await AgeCodesAsync(TimeSpan.FromMinutes(5));
        }

        await AgeCodesAsync(TimeSpan.FromDays(1));
        await RequestAsync();

        _delivery.Sent.Should().HaveCount(LoginCodeService.EmailsPerWindow + 1);
    }

    [Fact]
    public async Task ANewLink_VoidsTheEarlierLinkButNoAdministratorCode()
    {
        var memberId = await SeedMemberAsync("Rhys");
        await RequestAsync();
        var first = CodeIn(_delivery.Sent[0]);
        await AgeCodesAsync(TimeSpan.FromMinutes(5));
        await using (var db = _db.CreateContext(_tenantId))
            await _loginCodes.IssueAsync(db, memberId, LoginCodePurpose.Recovery, null, null, new AuthAuditActor(null, "test"));

        await RequestAsync();

        await using var check = _db.CreateContext(_tenantId);
        (await _loginCodes.FindLiveAsync(check, first, [LoginCodePurpose.EmailRecovery])).Should().BeNull();
        (await check.LoginCodes.CountAsync(c => c.Purpose == LoginCodePurpose.Recovery && c.ConsumedAt == null))
            .Should().Be(1);
    }

    [Fact]
    public async Task AnAdministratorCode_DoesNotVoidAnEmailedLink()
    {
        var memberId = await SeedMemberAsync("Rhys");
        await RequestAsync();

        await using (var db = _db.CreateContext(_tenantId))
            await _loginCodes.IssueAsync(db, memberId, LoginCodePurpose.OperatorRecovery, null, null, new AuthAuditActor(null, "test"));

        await using var check = _db.CreateContext(_tenantId);
        (await _loginCodes.FindLiveAsync(check, CodeIn(_delivery.Sent[0]), [LoginCodePurpose.EmailRecovery]))
            .Should().NotBeNull();
    }

    #endregion

    #region The request endpoint

    /// <summary>Holds on to the dispatched work, so a test can wait for what the response did not.</summary>
    private sealed class CapturingRequests(IServiceProvider services)
        : RecoveryLinkRequests(services, NullLogger<RecoveryLinkRequests>.Instance)
    {
        public List<Task> Started { get; } = [];

        public override Task RequestAsync(
            TenantContext tenant, string email, AuthAuditActor actor, string? ipAddress, string? userAgent)
        {
            var work = base.RequestAsync(tenant, email, actor, ipAddress, userAgent);
            Started.Add(work);
            return work;
        }
    }

    private sealed class SettableTenantAccessor : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;
        public void SetTenant(TenantContext context) => Context = context;
    }

    private CapturingRequests Requests()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor, SettableTenantAccessor>();
        services.AddScoped(_ => Links());
        return new CapturingRequests(services.BuildServiceProvider());
    }

    private PasskeyController Controller(Guid? tenantId = null)
    {
        var tenant = tenantId ?? _tenantId;
        var contexts = Contexts(tenant);
        var controller = new PasskeyController(
            new Mock<IPasskeyService>().Object,
            new SignInCompletion(new Mock<ITotpService>().Object, new TenantMemberService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System), new Mock<ISessionService>().Object, new Mock<ISubjectService>().Object, new Mock<IAuthAuditService>().Object),
            new Mock<ITrustedDeviceService>().Object,
            new Mock<IRecoveryCodeService>().Object,
            new RecoverySession(contexts, _loginCodes, new TenantMemberService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System), Options.Create(new OidcOptions()), NullLogger<RecoverySession>.Instance),
            new Mock<ISubjectService>().Object,
            new Mock<IAuthAuditService>().Object,
            MockTenantAccessor.Create(tenant).Object,
            new Mock<ITenantService>().Object,
            new TenantMemberService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System),
            new SubjectRepository(contexts, AccountLinkRoutes.Off),
            new TenantRepository(contexts),
            new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
            contexts,
            Options.Create(new OidcOptions()),
            NullLogger<PasskeyController>.Instance, TimeProvider.System);
        var http = new DefaultHttpContext();
        http.SetTenantContext(tenant == _tenantId ? _tenant : new TenantContext(tenant, "beta", "Beta", true, false));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    [Fact]
    public async Task TheRequest_AnswersTheSameForKnownUnknownUnconfirmedAndBlankAddresses()
    {
        await SeedMemberAsync("Known");
        await SeedMemberAsync("Unconfirmed", email: "u@example.test", confirmed: false);
        var requests = Requests();

        var answers = new List<IActionResult>();
        // One request's work at a time: the tests share one SQLite connection, which takes one
        // transaction at a time.
        foreach (var email in new[] { Address, "nobody@example.test", "u@example.test", "not an address", " " })
        {
            answers.Add(Controller().RequestRecoveryLink(new RecoveryLinkRequest { Email = email }, _delivery, requests));
            await Task.WhenAll(requests.Started);
        }

        answers.Should().AllBeOfType<NoContentResult>();
        _delivery.Sent.Should().ContainSingle().Which.AccountDisplayName.Should().Be("Known");
    }

    [Fact]
    public async Task TheRequest_AnswersBeforeTheDeliveryIsMade()
    {
        await SeedMemberAsync("Rhys");
        _delivery.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Requests();

        var answer = Controller().RequestRecoveryLink(new RecoveryLinkRequest { Email = Address }, _delivery, requests);

        answer.Should().BeOfType<NoContentResult>();
        _delivery.Sent.Should().BeEmpty("the response did not wait for the delivery");
        _delivery.Gate.SetResult();
        await Task.WhenAll(requests.Started);
        _delivery.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task TheRequest_WithAFailedDelivery_AnswersTheSame()
    {
        await SeedMemberAsync("Rhys");
        _delivery.Succeeds = false;
        var requests = Requests();

        var answer = Controller().RequestRecoveryLink(new RecoveryLinkRequest { Email = Address }, _delivery, requests);
        await Task.WhenAll(requests.Started);

        answer.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public void TheRequest_WithNoDeliveryRoute_Is404WithItsCode()
    {
        _delivery.IsConfigured = false;
        var requests = Requests();

        var answer = Controller().RequestRecoveryLink(new RecoveryLinkRequest { Email = Address }, _delivery, requests);

        var problem = answer.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        ((ProblemDetails)problem.Value!).Extensions["code"].Should().Be(AccountLinkProblemCodes.Unavailable);
        requests.Started.Should().BeEmpty();
    }

    #endregion

    #region Redeeming a recovery link

    private async Task<string> EmailedLinkAsync()
    {
        _delivery.Sent.Clear();
        await RequestAsync();
        return CodeIn(_delivery.Sent.Single());
    }

    private async Task<ActionResult<RecoveryModeRedeemResponse>> RedeemAsync(string code, PasskeyController? controller = null)
    {
        controller ??= Controller();
        var contexts = Contexts(_tenantId);
        return await controller.RecoveryModeRedeem(
            new RecoveryModeRedeemRequest { Code = code }, _loginCodes,
            new MemberRecoveryEligibility(new TenantMemberService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System), _delivery));
    }

    private static bool StartedASession(ActionResult<RecoveryModeRedeemResponse> result, ControllerBase controller) =>
        result.Result is OkObjectResult
        && controller.HttpContext.Response.Headers.SetCookie.Any(h => h!.StartsWith($"{RecoverySession.CookieName}=", StringComparison.Ordinal));

    private async Task EditSubjectAsync(Guid subjectId, Action<SubjectEntity> edit)
    {
        await using var db = _db.CreateContext(Guid.Empty);
        edit(await db.Subjects.SingleAsync(s => s.Id == subjectId));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ARecoveryLink_StartsASessionForAMemberWhoStillHasAWorkingPasskey()
    {
        var memberId = await SeedMemberAsync("Rhys");
        await using (var db = _db.CreateContext(Guid.Empty))
        {
            db.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = memberId,
                CredentialId = [1, 2],
                PublicKey = [1],
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var controller = Controller();

        var result = await RedeemAsync(await EmailedLinkAsync(), controller);

        StartedASession(result, controller).Should().BeTrue("the lost-device case still has a passkey registered");
    }

    [Fact]
    public async Task ARecoveryLink_IsRefusedAfterTheAddressChanged()
    {
        var memberId = await SeedMemberAsync("Rhys");
        var code = await EmailedLinkAsync();
        await EditSubjectAsync(memberId, s =>
        {
            s.SetEmail("b@example.test");
            s.ConfirmEmail("b@example.test", DateTime.UtcNow);
        });

        var result = await RedeemAsync(code);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ARecoveryLink_IsRefusedAfterTheConfirmationWasCleared()
    {
        var memberId = await SeedMemberAsync("Rhys");
        var code = await EmailedLinkAsync();
        await EditSubjectAsync(memberId, s =>
        {
            s.SetEmail(null);
            s.SetEmail(Address);
        });

        var result = await RedeemAsync(code);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ARecoveryLink_IsRefusedAfterTheMemberLeftTheSite()
    {
        var memberId = await SeedMemberAsync("Rhys");
        var code = await EmailedLinkAsync();
        await using (var db = _db.CreateContext(_tenantId))
        {
            db.TenantMembers.RemoveRange(db.TenantMembers.Where(m => m.SubjectId == memberId));
            await db.SaveChangesAsync();
        }

        var result = await RedeemAsync(code);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ARecoveryLink_IsRefusedOnAnotherSitesHost()
    {
        var memberId = await SeedMemberAsync("Rhys");
        await SeedMembershipAsync(memberId, _otherTenantId);
        var code = await EmailedLinkAsync();

        var result = await RedeemAsync(code, Controller(_otherTenantId));

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    private async Task SeedMembershipAsync(Guid subjectId, Guid tenantId)
    {
        await using var db = _db.CreateContext(tenantId);
        db.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = subjectId });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ARecoveryLink_IsRefusedOnceSpent()
    {
        await SeedMemberAsync("Rhys");
        var code = await EmailedLinkAsync();
        await RedeemAsync(code);

        var second = await RedeemAsync(code);

        second.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ARecoveryLink_IsRefusedOnceExpired()
    {
        await SeedMemberAsync("Rhys");
        var code = await EmailedLinkAsync();
        await using (var db = _db.CreateContext(_tenantId))
        {
            await db.LoginCodes.ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        var result = await RedeemAsync(code);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Theory]
    [InlineData(LoginCodePurpose.EmailConfirm)]
    [InlineData(LoginCodePurpose.RecoverySession)]
    [InlineData(LoginCodePurpose.Login)]
    public async Task ACodeOfAnotherPurpose_IsRefusedAndLeftUnspent(LoginCodePurpose purpose)
    {
        var memberId = await SeedMemberAsync("Rhys");
        string code;
        await using (var db = _db.CreateContext(_tenantId))
        {
            code = await _loginCodes.IssueAnyAsync(db, memberId, purpose);
        }

        var result = await RedeemAsync(code);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        await using var check = _db.CreateContext(_tenantId);
        (await _loginCodes.FindLiveAsync(check, code, [purpose])).Should().NotBeNull();
    }

    [Fact]
    public async Task ASessionThatCannotStart_LeavesTheCodeUnspent()
    {
        await SeedMemberAsync("Rhys");
        var code = await EmailedLinkAsync();
        var failing = new Mock<INocturneDbContextFactory>();
        failing.Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var contexts = Contexts(_tenantId);
        var controller = new PasskeyController(
            new Mock<IPasskeyService>().Object,
            new SignInCompletion(new Mock<ITotpService>().Object, new TenantMemberService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System), new Mock<ISessionService>().Object, new Mock<ISubjectService>().Object, new Mock<IAuthAuditService>().Object),
            new Mock<ITrustedDeviceService>().Object,
            new Mock<IRecoveryCodeService>().Object,
            new RecoverySession(failing.Object, _loginCodes, Mock.Of<ITenantMemberService>(), Options.Create(new OidcOptions()), NullLogger<RecoverySession>.Instance),
            new Mock<ISubjectService>().Object, new Mock<IAuthAuditService>().Object,
            MockTenantAccessor.Create(_tenantId).Object, new Mock<ITenantService>().Object,
            new TenantMemberService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System), new SubjectRepository(contexts, AccountLinkRoutes.Off),
            new TenantRepository(contexts), new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), contexts,
            Options.Create(new OidcOptions()), NullLogger<PasskeyController>.Instance, TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var redeeming = () => RedeemAsync(code, controller);

        await redeeming.Should().ThrowAsync<InvalidOperationException>();
        await using var check = _db.CreateContext(_tenantId);
        (await _loginCodes.FindLiveAsync(check, code, [LoginCodePurpose.EmailRecovery])).Should().NotBeNull();
        controller.HttpContext.Response.Headers.SetCookie.Should().BeEmpty();
    }

    [Fact]
    public async Task RedeemingARecoveryLink_InvalidatesTheMembersOwnRecoveryCodes()
    {
        var memberId = await SeedMemberAsync("Rhys");
        var recoveryCodes = new Mock<IRecoveryCodeService>();
        var contexts = Contexts(_tenantId);
        var controller = new PasskeyController(
            new Mock<IPasskeyService>().Object,
            new SignInCompletion(new Mock<ITotpService>().Object, new TenantMemberService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System), new Mock<ISessionService>().Object, new Mock<ISubjectService>().Object, new Mock<IAuthAuditService>().Object),
            new Mock<ITrustedDeviceService>().Object,
            recoveryCodes.Object,
            new RecoverySession(contexts, _loginCodes, new TenantMemberService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System), Options.Create(new OidcOptions()), NullLogger<RecoverySession>.Instance),
            new Mock<ISubjectService>().Object, new Mock<IAuthAuditService>().Object,
            MockTenantAccessor.Create(_tenantId).Object, new Mock<ITenantService>().Object,
            new TenantMemberService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System), new SubjectRepository(contexts, AccountLinkRoutes.Off),
            new TenantRepository(contexts), new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), contexts,
            Options.Create(new OidcOptions()), NullLogger<PasskeyController>.Instance, TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await RedeemAsync(await EmailedLinkAsync(), controller);

        result.Result.Should().BeOfType<OkObjectResult>();
        recoveryCodes.Verify(r => r.InvalidateAllAsync(memberId), Times.Once);
    }

    #endregion

    #region Lifetimes

    [Theory]
    [InlineData(LoginCodePurpose.EmailRecovery, 30)]
    [InlineData(LoginCodePurpose.EmailConfirm, 24 * 60)]
    [InlineData(LoginCodePurpose.RecoverySession, 10)]
    [InlineData(LoginCodePurpose.Recovery, 24 * 60)]
    [InlineData(LoginCodePurpose.OperatorRecovery, 24 * 60)]
    [InlineData(LoginCodePurpose.Login, 5)]
    public void EachPurpose_LivesItsOwnTime(LoginCodePurpose purpose, int minutes) =>
        LoginCodeService.LifetimeOf(purpose).Should().Be(TimeSpan.FromMinutes(minutes));

    [Fact]
    public void TheEmailedAndSessionPurposes_AreNotAuthorityRecoveryCodes() =>
        LoginCodeService.RecoveryPurposes.Should().NotContain(
            [LoginCodePurpose.EmailRecovery, LoginCodePurpose.EmailConfirm, LoginCodePurpose.RecoverySession]);

    #endregion
}
