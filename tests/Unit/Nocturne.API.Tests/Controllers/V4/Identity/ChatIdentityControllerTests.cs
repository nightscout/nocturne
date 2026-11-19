using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Chat;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// Covers what the tenant-scoped link list tells a member about a chat account whose default sits
/// on a link in a tenant this list does not show, and that a link the caller does not own is
/// neither disclosed to nor mutable by them.
/// </summary>
[Trait("Category", "Unit")]
public class ChatIdentityControllerTests : IDisposable
{
    private const string Platform = "discord";
    private const string CallerChatUser = "discord-user-a";
    private const string CoMemberChatUser = "discord-user-b";

    private readonly SqliteTestDatabase _db;
    private readonly ChatIdentityService _service;
    private readonly Guid _tenantId;
    private readonly Guid _otherTenantId;
    private readonly Guid _callerSubjectId;
    private readonly Guid _coMemberSubjectId;

    public ChatIdentityControllerTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _service = new ChatIdentityService(
            new ChatIdentityDirectoryService(
                _db.ContextFactory.AsNocturneFactory(), TimeProvider.System, Mock.Of<ILogger<ChatIdentityDirectoryService>>()),
            new ChatIdentityPendingLinkService(
                _db.ContextFactory.AsNocturneFactory(), TimeProvider.System, Mock.Of<ILogger<ChatIdentityPendingLinkService>>()),
            _db.ContextFactory.AsNocturneFactory(),
            new TenantRepository(_db.ContextFactory.AsNocturneFactory()),
            TimeProvider.System, Mock.Of<ILogger<ChatIdentityService>>());

        _tenantId = NewTenant();
        _otherTenantId = NewTenant();
        _callerSubjectId = NewSubject();
        _coMemberSubjectId = NewSubject();
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// Inserts a tenant and returns its id. chat_identity_directory.tenant_id is a real FK, so a
    /// link cannot point at a tenant that was never created.
    /// </summary>
    private Guid NewTenant()
    {
        var id = Guid.CreateVersion7();
        using var db = _db.CreateContext();
        db.Tenants.Add(new TenantEntity
        {
            Id = id,
            Slug = $"t-{id:n}"[..20],
            DisplayName = "Test Tenant",
        });
        db.SaveChanges();
        return id;
    }

    /// <summary>
    /// Inserts a subject and returns its id. chat_identity_directory.nocturne_user_id is a real
    /// FK, so a link cannot point at a subject that was never created.
    /// </summary>
    private Guid NewSubject()
    {
        var id = Guid.CreateVersion7();
        using var db = _db.CreateContext();
        db.Subjects.Add(new SubjectEntity { Id = id, Name = $"s-{id:n}"[..20] });
        db.SaveChanges();
        return id;
    }

    private Guid InsertLink(
        Guid tenantId, Guid subjectId, string label, bool isDefault,
        string platformUserId = CallerChatUser)
    {
        var id = Guid.CreateVersion7();
        using var db = _db.CreateContext();
        db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
        {
            Id = id,
            Platform = Platform,
            PlatformUserId = platformUserId,
            TenantId = tenantId,
            NocturneUserId = subjectId,
            Label = label,
            DisplayName = label,
            IsDefault = isDefault,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    /// <summary>
    /// Issues a pending link token, by default with no tenant slug hint, which any tenant may claim.
    /// </summary>
    private string InsertPendingToken(string platformUserId, string? tenantSlug = null)
    {
        var token = Convert.ToHexString(Guid.CreateVersion7().ToByteArray());
        using var db = _db.CreateContext();
        db.ChatIdentityPendingLinks.Add(new ChatIdentityPendingLinkEntity
        {
            Token = token,
            Platform = Platform,
            PlatformUserId = platformUserId,
            TenantSlug = tenantSlug,
            Source = "connect-slash",
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        });
        db.SaveChanges();
        return token;
    }

    private ChatIdentityController CreateController(bool withSubject = true, Guid? tenantId = null)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(tenantId ?? _tenantId);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = withSubject ? _callerSubjectId : null,
            TenantId = _tenantId,
        };

        return new ChatIdentityController(_service, tenantAccessor.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private async Task<bool> LinkExists(Guid id)
    {
        await using var db = _db.CreateContext();
        return await db.ChatIdentityDirectory.AnyAsync(d => d.Id == id);
    }

    private async Task<ChatIdentityDirectoryEntry> Reload(Guid id)
    {
        await using var db = _db.CreateContext();
        return await db.ChatIdentityDirectory.AsNoTracking().SingleAsync(d => d.Id == id);
    }

    private async Task<List<ChatIdentityDirectoryEntry>> LinksFor(string platformUserId)
    {
        await using var db = _db.CreateContext();
        return await db.ChatIdentityDirectory.AsNoTracking()
            .Where(d => d.PlatformUserId == platformUserId)
            .ToListAsync();
    }

    private static void AssertProblem(ActionResult<ChatIdentityLinkResponse> result, int status)
    {
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(status);
        problem.Value.Should().BeOfType<ProblemDetails>();
    }

    private static ChatIdentityLinkResponse OkLink(ActionResult<ChatIdentityLinkResponse> result)
        => result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ChatIdentityLinkResponse>().Subject;

    private static ChatIdentityLinkResponse SingleLink(ActionResult<List<ChatIdentityLinkResponse>> result)
        => result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<List<ChatIdentityLinkResponse>>().Subject
            .Should().ContainSingle().Subject;

    [Fact]
    public async Task GetLinks_names_the_link_holding_the_default_when_it_belongs_to_another_tenant()
    {
        InsertLink(_tenantId, _callerSubjectId, "lily", isDefault: false);
        InsertLink(_otherTenantId, _callerSubjectId, "oliver", isDefault: true);

        var link = SingleLink(await CreateController().GetLinks(CancellationToken.None));

        link.Label.Should().Be("lily");
        link.IsDefault.Should().BeFalse();
        link.DefaultLabel.Should().Be("oliver");
    }

    [Fact]
    public async Task GetLinks_leaves_the_default_label_off_a_link_belonging_to_another_subject()
    {
        InsertLink(_tenantId, _coMemberSubjectId, "lily", isDefault: false, platformUserId: CoMemberChatUser);
        InsertLink(_otherTenantId, _coMemberSubjectId, "oliver", isDefault: true, platformUserId: CoMemberChatUser);

        var link = SingleLink(await CreateController().GetLinks(CancellationToken.None));

        link.DefaultLabel.Should().BeNull(
            "the label is the slug of a tenant the caller may have no part in");
    }

    [Fact]
    public async Task GetLinks_reports_no_default_label_when_no_link_holds_the_default()
    {
        InsertLink(_tenantId, _callerSubjectId, "lily", isDefault: false);
        InsertLink(_otherTenantId, _callerSubjectId, "oliver", isDefault: false);

        var link = SingleLink(await CreateController().GetLinks(CancellationToken.None));

        link.DefaultLabel.Should().BeNull();
    }

    [Fact]
    public async Task GetLinks_still_lists_for_a_credential_that_carries_no_subject()
    {
        InsertLink(_tenantId, _callerSubjectId, "lily", isDefault: false);
        InsertLink(_otherTenantId, _callerSubjectId, "oliver", isDefault: true);

        var link = SingleLink(await CreateController(withSubject: false).GetLinks(CancellationToken.None));

        link.Label.Should().Be("lily");
        link.DefaultLabel.Should().BeNull();
    }

    [Fact]
    public async Task GetLinks_returns_the_platform_user_id_on_the_callers_own_link()
    {
        InsertLink(_tenantId, _callerSubjectId, "lily", isDefault: true);

        var link = SingleLink(await CreateController().GetLinks(CancellationToken.None));

        link.PlatformUserId.Should().Be(CallerChatUser);
    }

    [Fact]
    public async Task GetLinks_withholds_the_platform_user_id_on_a_co_members_link()
    {
        InsertLink(_tenantId, _coMemberSubjectId, "oliver", isDefault: true, platformUserId: CoMemberChatUser);

        var link = SingleLink(await CreateController().GetLinks(CancellationToken.None));

        link.Label.Should().Be("oliver", "the row is still listed");
        link.PlatformUserId.Should().BeNull(
            "a co-member's chat account is theirs, and enumerating it is not a tenant privilege");
    }

    [Fact]
    public async Task GetLinks_marks_the_callers_own_link_as_owned()
    {
        InsertLink(_tenantId, _callerSubjectId, "lily", isDefault: true);

        var link = SingleLink(await CreateController().GetLinks(CancellationToken.None));

        link.IsOwnedByCaller.Should().BeTrue();
    }

    [Fact]
    public async Task GetLinks_marks_a_co_members_link_as_not_owned()
    {
        InsertLink(_tenantId, _coMemberSubjectId, "oliver", isDefault: true, platformUserId: CoMemberChatUser);

        var link = SingleLink(await CreateController().GetLinks(CancellationToken.None));

        link.IsOwnedByCaller.Should().BeFalse(
            "the settings page offers set-default, edit and revoke on this flag, and all three 404");
    }

    [Fact]
    public async Task GetPending_names_the_chat_account_the_token_would_link()
    {
        var token = await new ChatIdentityPendingLinkService(
                _db.ContextFactory.AsNocturneFactory(), TimeProvider.System, Mock.Of<ILogger<ChatIdentityPendingLinkService>>())
            .CreateAsync(Platform, CallerChatUser, "sam_t", null, "connect-slash", CancellationToken.None);

        var result = await CreateController().GetPending(token, CancellationToken.None);

        var pending = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<PendingLinkViewResponse>().Subject;
        pending.PlatformUserName.Should().Be("sam_t");
        pending.PlatformUserId.Should().Be(CallerChatUser);
    }

    [Fact]
    public async Task ClaimLink_reports_the_new_link_as_owned()
    {
        var token = InsertPendingToken(CallerChatUser);

        var result = await CreateController().ClaimLink(
            new ClaimChatIdentityLinkRequest { Token = token }, CancellationToken.None);

        var link = OkLink(result);
        link.NocturneUserId.Should().Be(_callerSubjectId);
        link.IsOwnedByCaller.Should().BeTrue();
        link.PlatformUserId.Should().Be(CallerChatUser);
    }

    [Fact]
    public async Task ClaimLink_answers_conflict_when_the_chat_account_is_linked_to_a_co_member()
    {
        var coMemberLink = InsertLink(_tenantId, _coMemberSubjectId, "oliver", isDefault: true);
        var token = InsertPendingToken(CallerChatUser);

        var result = await CreateController().ClaimLink(
            new ClaimChatIdentityLinkRequest { Token = token }, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status409Conflict);
        (await Reload(coMemberLink)).NocturneUserId.Should().Be(_coMemberSubjectId);
    }

    [Fact]
    public async Task ClaimLink_answers_bad_request_for_a_token_that_was_never_issued()
    {
        var result = await CreateController().ClaimLink(
            new ClaimChatIdentityLinkRequest { Token = "never-issued" }, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ClaimLink_answers_bad_request_for_a_token_issued_for_another_tenant()
    {
        var token = InsertPendingToken(CallerChatUser, tenantSlug: "someone-else");

        var result = await CreateController().ClaimLink(
            new ClaimChatIdentityLinkRequest { Token = token }, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest);
        (await LinksFor(CallerChatUser)).Should().BeEmpty();
    }

    [Fact]
    public async Task ClaimLink_returns_the_callers_existing_link_again()
    {
        var existing = InsertLink(_tenantId, _callerSubjectId, "lily", isDefault: true);
        var token = InsertPendingToken(CallerChatUser);

        var result = await CreateController().ClaimLink(
            new ClaimChatIdentityLinkRequest { Token = token }, CancellationToken.None);

        var link = OkLink(result);
        link.Id.Should().Be(existing);
        link.IsOwnedByCaller.Should().BeTrue();
    }

    [Fact]
    public async Task RevokeLink_refuses_a_co_members_link()
    {
        var coMemberLink = InsertLink(
            _tenantId, _coMemberSubjectId, "oliver", isDefault: true, platformUserId: CoMemberChatUser);

        var result = await CreateController().RevokeLink(coMemberLink, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        (await LinkExists(coMemberLink)).Should().BeTrue();
    }

    [Fact]
    public async Task RevokeLink_refuses_the_callers_own_link_in_another_tenant()
    {
        var elsewhere = InsertLink(_otherTenantId, _callerSubjectId, "oliver", isDefault: true);

        var result = await CreateController().RevokeLink(elsewhere, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        (await LinkExists(elsewhere)).Should().BeTrue();
    }

    [Fact]
    public async Task RevokeLink_refuses_a_credential_that_carries_no_subject()
    {
        var link = InsertLink(_tenantId, _callerSubjectId, "lily", isDefault: true);

        var act = async () => await CreateController(withSubject: false)
            .RevokeLink(link, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await LinkExists(link)).Should().BeTrue();
    }

    [Fact]
    public async Task RevokeLink_refuses_a_request_whose_tenant_did_not_resolve()
    {
        var link = InsertLink(_tenantId, _callerSubjectId, "lily", isDefault: true);

        var act = async () => await CreateController(tenantId: Guid.Empty)
            .RevokeLink(link, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await LinkExists(link)).Should().BeTrue();
    }

    [Fact]
    public async Task RevokeLink_removes_the_callers_own_link()
    {
        var own = InsertLink(_tenantId, _callerSubjectId, "lily", isDefault: true);

        var result = await CreateController().RevokeLink(own, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await LinkExists(own)).Should().BeFalse();
    }

    [Fact]
    public async Task SetDefault_refuses_a_co_members_link_and_leaves_their_default_alone()
    {
        var coMemberHere = InsertLink(
            _tenantId, _coMemberSubjectId, "oliver", isDefault: false, platformUserId: CoMemberChatUser);
        var coMemberElsewhere = InsertLink(
            _otherTenantId, _coMemberSubjectId, "rose", isDefault: true, platformUserId: CoMemberChatUser);

        var result = await CreateController().SetDefault(coMemberHere, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        (await Reload(coMemberHere)).IsDefault.Should().BeFalse();
        (await Reload(coMemberElsewhere)).IsDefault.Should().BeTrue(
            "a co-member's bare bot commands keep resolving to the tenant they chose");
    }

    [Fact]
    public async Task SetDefault_promotes_the_callers_own_link()
    {
        var own = InsertLink(_tenantId, _callerSubjectId, "lily", isDefault: false);

        var result = await CreateController().SetDefault(own, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await Reload(own)).IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateLink_refuses_a_co_members_link()
    {
        var coMemberLink = InsertLink(
            _tenantId, _coMemberSubjectId, "oliver", isDefault: true, platformUserId: CoMemberChatUser);

        var result = await CreateController().UpdateLink(
            coMemberLink,
            new UpdateChatIdentityLinkRequest { Label = "stolen", DisplayName = "Stolen" },
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        var unchanged = await Reload(coMemberLink);
        unchanged.Label.Should().Be("oliver");
        unchanged.DisplayName.Should().Be("oliver");
    }

    [Fact]
    public async Task UpdateLink_renames_the_callers_own_link()
    {
        var own = InsertLink(_tenantId, _callerSubjectId, "lily", isDefault: true);

        var result = await CreateController().UpdateLink(
            own,
            new UpdateChatIdentityLinkRequest { Label = "rose", DisplayName = "Rose" },
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        var renamed = await Reload(own);
        renamed.Label.Should().Be("rose");
        renamed.DisplayName.Should().Be("Rose");
    }
}
