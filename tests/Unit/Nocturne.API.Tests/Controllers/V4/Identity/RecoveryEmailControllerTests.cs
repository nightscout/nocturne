using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// A member's own recovery email: what is read back, what a write stores and sends, and what no
/// write confirms. The real repository, subject service and link service run over SQLite.
/// </summary>
[Trait("Category", "Unit")]
public class RecoveryEmailControllerTests : IDisposable
{
    private sealed class SharedSqliteFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    private const string Address = "a@example.test";
    private const string Other = "b@example.test";

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly TenantContext _tenant;
    private readonly EmailRecoveryTests.RecordingDelivery _delivery = new();
    private readonly LoginCodeService _loginCodes;
    private readonly INocturneDbContextFactory _contexts;
    private readonly Mock<IAuthAuditService> _audit = new();
    private readonly List<(string EventType, string? Details)> _audited = [];

    public RecoveryEmailControllerTests()
    {
        _tenant = new TenantContext(_tenantId, "alpha", "Alpha household", true, false);
        _audit.Setup(a => a.LogAsync(
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()))
            .Callback<string, Guid?, bool, string?, string?, string?, string?, Guid?, AuthAuditActor?, Guid?>(
                (eventType, _, _, _, _, _, details, _, _, _) => _audited.Add((eventType, details)))
            .Returns(Task.CompletedTask);
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.GenerateRefreshToken()).Returns(() => Guid.NewGuid().ToString("N"));
        _loginCodes = new LoginCodeService(jwt.Object, new Mock<IAuthAuditService>().Object, TimeProvider.System);
        _contexts = new SharedSqliteFactory(_db.Options).AsNocturneFactory(_tenantId);

        using var seed = _db.CreateContext(Guid.Empty);
        seed.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "alpha", DisplayName = "Alpha household" });
        seed.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private SubjectRepository Repository() => new(_contexts, AccountLinkRoutes.Off);

    private RecoveryEmailController Controller(
        Guid? signedIn, AuthType authType = AuthType.SessionCookie, bool deliveryConfigured = true,
        string sessionId = LiveSession.Id)
    {
        _delivery.IsConfigured = deliveryConfigured;
        var subjects = Repository();
        var http = new DefaultHttpContext { RequestServices = TestRequestServices.Build(clamped: false) };
        http.SetTenantContext(_tenant);
        http.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true, AuthType = authType, SubjectId = signedIn, TenantId = _tenantId,
            SessionId = sessionId,
        };
        var links = new AccountLinkService(
            _contexts, _loginCodes, subjects, _delivery,
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example" }),
            TimeProvider.System);
        var service = new SubjectService(
            subjects, _audit.Object, Mock.Of<IRecoveryCodeService>(), NullLogger<SubjectService>.Instance, TimeProvider.System);
        return new RecoveryEmailController(subjects, service, links, AccountLinkRoutes.Of(deliveryConfigured))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private async Task<Guid> SeedMemberAsync(string? email = null, bool confirmed = false, bool demo = false)
    {
        var id = Guid.CreateVersion7();
        await using var db = _db.CreateContext(Guid.Empty);
        var subject = new SubjectEntity { Id = id, Name = "Rhys", IsActive = true, IsDemoSubject = demo };
        subject.SetEmail(email);
        if (confirmed)
            subject.ConfirmEmail(email, DateTime.UtcNow);
        db.Subjects.Add(subject);
        db.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = id });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<SubjectEntity> StoredAsync(Guid id)
    {
        await using var db = _db.CreateContext(Guid.Empty);
        return await db.Subjects.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private static async Task<RecoveryEmailResponse> ReadAsync(RecoveryEmailController controller) =>
        (await controller.GetRecoveryEmail(default)).Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<RecoveryEmailResponse>().Subject;

    private static void ShouldBeProblem(IActionResult result, int status, string code)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(status);
        ((ProblemDetails)problem.Value!).Extensions["code"].Should().Be(code);
    }

    private static SetRecoveryEmailResponse Outcome(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<SetRecoveryEmailResponse>().Subject;

    private static SetRecoveryEmailRequest Request(string email) => new() { Email = email };

    [Fact]
    public async Task Get_ForAMemberWithNoAddress_IsAvailableAndEmpty()
    {
        var id = await SeedMemberAsync();

        var response = await ReadAsync(Controller(id));

        response.Should().BeEquivalentTo(new RecoveryEmailResponse { Available = true, Email = null, Confirmed = false });
    }

    [Fact]
    public async Task Get_ForAnUnconfirmedAddress_ReturnsItUnconfirmed()
    {
        var id = await SeedMemberAsync(Address);

        var response = await ReadAsync(Controller(id));

        response.Should().BeEquivalentTo(new RecoveryEmailResponse { Available = true, Email = Address, Confirmed = false });
    }

    [Fact]
    public async Task Get_ForAConfirmedAddress_ReturnsItConfirmed()
    {
        var id = await SeedMemberAsync(Address, confirmed: true);

        var response = await ReadAsync(Controller(id));

        response.Should().BeEquivalentTo(new RecoveryEmailResponse { Available = true, Email = Address, Confirmed = true });
    }

    [Fact]
    public async Task Get_WithNoDeliveryRoute_SaysUnavailableAndGivesNoAddress()
    {
        var id = await SeedMemberAsync(Address, confirmed: true);

        var response = await ReadAsync(Controller(id, deliveryConfigured: false));

        response.Should().BeEquivalentTo(new RecoveryEmailResponse());
    }

    [Fact]
    public async Task Get_ForTheDemoSubject_SaysUnavailable()
    {
        var id = await SeedMemberAsync(Address, demo: true);

        (await ReadAsync(Controller(id))).Should().BeEquivalentTo(new RecoveryEmailResponse());
    }

    [Theory]
    [InlineData(AuthType.PlatformAccess)]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.InstanceKey)]
    public async Task ACredentialThatIsNotTheAccount_ReadsNothingAndWritesNothing(AuthType authType)
    {
        var id = await SeedMemberAsync(Address);
        var controller = Controller(id, authType);

        (await ReadAsync(controller)).Should().BeEquivalentTo(new RecoveryEmailResponse());
        (await controller.SetRecoveryEmail(Request(Other), default)).Should().BeOfType<ForbidResult>();
        (await controller.DeleteRecoveryEmail()).Should().BeOfType<ForbidResult>();

        (await StoredAsync(id)).Email.Should().Be(Address);
        _delivery.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_ANewAddress_IsStoredUnconfirmedAndTheLinkIsForIt()
    {
        var id = await SeedMemberAsync();

        Outcome(await Controller(id).SetRecoveryEmail(Request($"  {Address} "), default))
            .Should().BeEquivalentTo(new SetRecoveryEmailResponse { Email = Address, Confirmed = false, Sent = true });

        var stored = await StoredAsync(id);
        stored.Email.Should().Be(Address, "the trimmed value is stored");
        stored.EmailConfirmedAt.Should().BeNull("saving an address never confirms it");
        var message = _delivery.Sent.Should().ContainSingle().Subject;
        message.Kind.Should().Be(AccountLinkKind.EmailConfirmation);
        message.Email.Should().Be(Address);
        message.Link.Should().StartWith("https://alpha.nocturne.example/auth/confirm-email?code=");
    }

    [Fact]
    public async Task Put_FromAnEndedSession_IsChallengedChangesNothingAndSendsNothing()
    {
        var id = await SeedMemberAsync(Address, confirmed: true);

        var result = await Controller(id, sessionId: "ended-session").SetRecoveryEmail(Request(Other), default);

        result.Should().BeOfType<ChallengeResult>();
        var stored = await StoredAsync(id);
        stored.Email.Should().Be(Address);
        stored.EmailConfirmedAt.Should().NotBeNull();
        _delivery.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_ADifferentAddress_ClearsTheConfirmationAndSendsTheLinkToTheNewOne()
    {
        var id = await SeedMemberAsync(Address, confirmed: true);

        Outcome(await Controller(id).SetRecoveryEmail(Request(Other), default)).Sent.Should().BeTrue();

        var stored = await StoredAsync(id);
        stored.Email.Should().Be(Other);
        stored.EmailConfirmedAt.Should().BeNull();
        _delivery.Sent.Should().ContainSingle().Which.Email.Should().Be(Other);
    }

    [Theory]
    [InlineData("a@example.test")]
    [InlineData("  A@Example.TEST ")]
    public async Task Put_TheConfirmedAddressAgain_ChangesNothingAndSendsNothing(string again)
    {
        var id = await SeedMemberAsync(Address, confirmed: true);
        var before = await StoredAsync(id);

        Outcome(await Controller(id).SetRecoveryEmail(Request(again), default))
            .Should().BeEquivalentTo(new SetRecoveryEmailResponse { Email = Address, Confirmed = true, Sent = false });

        var after = await StoredAsync(id);
        after.Email.Should().Be(Address);
        after.EmailConfirmedAt.Should().Be(before.EmailConfirmedAt).And.NotBeNull();
        _delivery.Sent.Should().BeEmpty();
        _audited.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_TheSameUnconfirmedAddress_SendsTheLinkAgainOnceTheThrottleAllowsIt()
    {
        var id = await SeedMemberAsync(Address);
        Outcome(await Controller(id).SetRecoveryEmail(Request(Address), default)).Sent.Should().BeTrue();

        ShouldBeProblem(
            await Controller(id).SetRecoveryEmail(Request(Address), default), 429, AccountLinkProblemCodes.Throttled);

        _delivery.Sent.Should().ContainSingle();
        (await StoredAsync(id)).Email.Should().Be(Address);
    }

    [Fact]
    public async Task Put_TheSameUnconfirmedAddressInAnotherCase_KeepsTheStoredSpellingAndStaysUnconfirmed()
    {
        var id = await SeedMemberAsync("Mixed.Case@example.test");

        var outcome = Outcome(await Controller(id).SetRecoveryEmail(Request("  MIXED.case@EXAMPLE.test "), default));

        outcome.Should().BeEquivalentTo(new SetRecoveryEmailResponse { Email = "Mixed.Case@example.test", Confirmed = false, Sent = true });
        var stored = await StoredAsync(id);
        stored.Email.Should().Be("Mixed.Case@example.test");
        stored.EmailConfirmedAt.Should().BeNull();
        _delivery.Sent.Should().ContainSingle().Which.Email.Should().Be("Mixed.Case@example.test");
    }

    [Fact]
    public async Task ASavedAddressIsFoundByTheRecoveryLookupWhenTypedPlainlyInAnotherCase()
    {
        var id = await SeedMemberAsync();
        await Controller(id).SetRecoveryEmail(Request(Address), default);
        await Repository().ConfirmEmailAsync(id, Address, DateTime.UtcNow, default);

        var found = await Repository().FindMembersByConfirmedEmailAsync(_tenantId, "  A@Example.TEST ", DateTime.UtcNow, default);

        found.Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task Put_WithNoDeliveryRoute_Is404AndStoresNothing()
    {
        var id = await SeedMemberAsync();

        ShouldBeProblem(
            await Controller(id, deliveryConfigured: false).SetRecoveryEmail(Request(Address), default),
            404, AccountLinkProblemCodes.Unavailable);

        (await StoredAsync(id)).Email.Should().BeNull();
        _delivery.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_WhenDeliveryFails_KeepsTheAddressAndAnswers502()
    {
        var id = await SeedMemberAsync();
        _delivery.Succeeds = false;

        ShouldBeProblem(
            await Controller(id).SetRecoveryEmail(Request(Address), default), 502, AccountLinkProblemCodes.DeliveryFailed);

        (await StoredAsync(id)).Email.Should().Be(Address);
    }

    [Fact]
    public async Task Put_IsAuditedAsASubjectUpdateWithNoAddressInTheDetails()
    {
        var id = await SeedMemberAsync();

        await Controller(id).SetRecoveryEmail(Request(Address), default);

        var entry = _audited.Should().ContainSingle().Subject;
        entry.EventType.Should().Be(AuthAuditEventType.SubjectUpdated);
        entry.Details.Should().NotContain(Address);
    }

    [Fact]
    public async Task Delete_ClearsTheAddressAndItsConfirmation()
    {
        var id = await SeedMemberAsync(Address, confirmed: true);

        (await Controller(id).DeleteRecoveryEmail()).Should().BeOfType<NoContentResult>();

        var stored = await StoredAsync(id);
        stored.Email.Should().BeNull();
        stored.EmailConfirmedAt.Should().BeNull();
        _delivery.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task NothingHereConfirmsAnAddress()
    {
        var id = await SeedMemberAsync();
        var controller = Controller(id);

        await controller.SetRecoveryEmail(Request(Address), default);
        await ReadAsync(controller);
        await controller.SetRecoveryEmail(Request(Other), default);

        (await StoredAsync(id)).EmailConfirmedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("two@@example.test")]
    [InlineData("a@example.test, b@example.test")]
    [InlineData("a@example.test; b@example.test")]
    [InlineData("Name <a@example.test>")]
    [InlineData("<a@example.test>")]
    [InlineData("\"Name\" a@example.test")]
    [InlineData("a b@example.test")]
    [InlineData("a@exam ple.test")]
    [InlineData("a@x")]
    [InlineData("@example.test")]
    [InlineData("a@")]
    [InlineData("a\t@example.test")]
    [InlineData("a@example\n.test")]
    [InlineData("a\u0000@example.test")]
    [InlineData("a\u0085@example.test")]
    [InlineData("a\u2028@example.test")]
    [InlineData("a\u200b@example.test")]
    [InlineData("a@example.test\u202e")]
    public void TheRequestRefusesAnythingButOneBareAddress(string bad)
    {
        Validate(bad).Should().BeFalse("it is not one bare address");
    }

    [Theory]
    [InlineData("a@example.test")]
    [InlineData("  A.B+tag@Example.Test  ")]
    public void TheRequestAcceptsOneBareAddressEvenWithSurroundingSpaces(string good)
    {
        Validate(good).Should().BeTrue();
    }

    [Fact]
    public void TheRequestRefusesAnAddressThatDoesNotFitTheColumn()
    {
        Validate(new string('a', 250) + "@example.test").Should().BeFalse();
    }

    private static bool Validate(string email)
    {
        var request = new SetRecoveryEmailRequest { Email = email };
        return Validator.TryValidateObject(request, new ValidationContext(request), new List<ValidationResult>(), true);
    }
}
