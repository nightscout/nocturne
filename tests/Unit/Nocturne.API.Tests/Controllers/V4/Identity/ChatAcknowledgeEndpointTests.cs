using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Chat;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// A chat acknowledgement is judged on the linked member, never on the bot's instance key, which
/// holds <c>*</c> and would acknowledge for everyone whoever tapped the button.
/// </summary>
[Trait("Category", "Unit")]
public class ChatAcknowledgeEndpointTests : IDisposable
{
    private const string Platform = "discord";
    private const string ChatUser = "chat-user-1";

    private readonly SqliteTestDatabase _db;
    private readonly Mock<IAlertAcknowledgementService> _ackService = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly Guid _tenantId;
    private readonly Guid _subjectId;

    public ChatAcknowledgeEndpointTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _tenantId = NewTenant();
        _subjectId = NewSubject();

        _tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        _tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);

        _ackService
            .Setup(s => s.AcknowledgeExcursionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<AlertAcknowledgementAuthority>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AlertAcknowledgementOutcome.Muted);
    }

    public void Dispose() => _db.Dispose();

    private ChatIdentityDirectoryController CreateController()
    {
        var tenantFactory = new Mock<ITenantDbContextFactory>();
        tenantFactory
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(_db.CreateContext(_tenantId)));

        return new ChatIdentityDirectoryController(
            new ChatIdentityDirectoryService(
                _db.ContextFactory, Mock.Of<ILogger<ChatIdentityDirectoryService>>()),
            new ChatIdentityPendingLinkService(
                _db.ContextFactory, Mock.Of<ILogger<ChatIdentityPendingLinkService>>()),
            _db.ContextFactory,
            tenantFactory.Object,
            _tenantAccessor.Object,
            _ackService.Object);
    }

    private Guid NewTenant()
    {
        var id = Guid.CreateVersion7();
        using var db = _db.CreateContext();
        db.Tenants.Add(new TenantEntity { Id = id, Slug = $"t-{id:n}"[..20], DisplayName = "Test Tenant" });
        db.SaveChanges();
        return id;
    }

    private Guid NewSubject()
    {
        var id = Guid.CreateVersion7();
        using var db = _db.CreateContext();
        db.Subjects.Add(new SubjectEntity { Id = id, Name = $"s-{id:n}"[..20] });
        db.SaveChanges();
        return id;
    }

    private Guid InsertLink(Guid tenantId, bool isActive = true)
    {
        var id = Guid.CreateVersion7();
        using var db = _db.CreateContext();
        db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
        {
            Id = id,
            Platform = Platform,
            PlatformUserId = ChatUser,
            TenantId = tenantId,
            NocturneUserId = _subjectId,
            Label = "home",
            DisplayName = "home",
            IsDefault = true,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private Guid InsertExcursion(DateTime? endedAt = null, string? acknowledgedBy = null)
    {
        var id = Guid.CreateVersion7();
        using var db = _db.CreateContext(_tenantId);
        var rule = new AlertRuleEntity { Id = Guid.CreateVersion7(), TenantId = _tenantId, Name = "Low" };
        db.AlertRules.Add(rule);
        db.AlertExcursions.Add(new AlertExcursionEntity
        {
            Id = id,
            TenantId = _tenantId,
            AlertRuleId = rule.Id,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            EndedAt = endedAt,
            AcknowledgedAt = acknowledgedBy is null ? null : DateTime.UtcNow.AddMinutes(-1),
            AcknowledgedBy = acknowledgedBy,
        });
        db.SaveChanges();
        return id;
    }

    private static ChatAcknowledgeRequest Request(Guid? excursionId, string platformUserId = ChatUser) => new()
    {
        Platform = Platform,
        PlatformUserId = platformUserId,
        ExcursionId = excursionId,
        AcknowledgedBy = "Sam Tester",
    };

    private void VerifyJudgedOnLinkedMember(Guid excursionId) =>
        _ackService.Verify(s => s.AcknowledgeExcursionAsync(
            _tenantId,
            excursionId,
            "Sam Tester",
            It.Is<AlertAcknowledgementAuthority>(a => a.SubjectId == _subjectId && a.GrantedScopes.Count == 0),
            true,
            It.IsAny<CancellationToken>()), Times.Once);

    private void VerifyNothingAcknowledged() =>
        _ackService.Verify(s => s.AcknowledgeExcursionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<AlertAcknowledgementAuthority>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);

    private void AcknowledgeReturns(AlertAcknowledgementOutcome outcome) =>
        _ackService
            .Setup(s => s.AcknowledgeExcursionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<AlertAcknowledgementAuthority>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

    [Fact]
    public async Task Excursion_IsDecidedOnTheLinkedMemberWithNoScopesOfItsOwn_AndReportsAMute()
    {
        var linkId = InsertLink(_tenantId);
        var excursionId = InsertExcursion();

        var result = await CreateController().AcknowledgeAsLinkedMember(
            linkId, Request(excursionId), CancellationToken.None);

        var response = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ChatAcknowledgeResponse>().Subject;
        response.Outcome.Should().Be(AlertAcknowledgementOutcome.Muted);
        response.AcknowledgedBy.Should().BeNull();
        response.AlreadyAcknowledged.Should().BeFalse();
        VerifyJudgedOnLinkedMember(excursionId);
    }

    [Fact]
    public async Task Excursion_AcknowledgedByThisRequest_CreditsTheChatUser()
    {
        var linkId = InsertLink(_tenantId);
        var excursionId = InsertExcursion();
        AcknowledgeReturns(AlertAcknowledgementOutcome.Acknowledged);

        var result = await CreateController().AcknowledgeAsLinkedMember(
            linkId, Request(excursionId), CancellationToken.None);

        var response = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ChatAcknowledgeResponse>().Subject;
        response.Outcome.Should().Be(AlertAcknowledgementOutcome.Acknowledged);
        response.AcknowledgedBy.Should().Be("Sam Tester");
        response.AlreadyAcknowledged.Should().BeFalse();
        VerifyJudgedOnLinkedMember(excursionId);
    }

    [Fact]
    public async Task Excursion_SomeoneElseAlreadyAcknowledged_NamesThemNotTheChatUser()
    {
        var linkId = InsertLink(_tenantId);
        var excursionId = InsertExcursion(acknowledgedBy: "Alex Owner");
        AcknowledgeReturns(AlertAcknowledgementOutcome.Acknowledged);

        var result = await CreateController().AcknowledgeAsLinkedMember(
            linkId, Request(excursionId), CancellationToken.None);

        var response = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ChatAcknowledgeResponse>().Subject;
        response.Outcome.Should().Be(AlertAcknowledgementOutcome.Acknowledged);
        response.AcknowledgedBy.Should().Be("Alex Owner");
        response.AlreadyAcknowledged.Should().BeTrue();
    }

    [Fact]
    public async Task NoExcursion_OneAcknowledgedByThisRequest_CreditsTheChatUserOverAnEarlierAcknowledger()
    {
        var linkId = InsertLink(_tenantId);
        InsertExcursion(acknowledgedBy: "Alex Owner");
        InsertExcursion();
        AcknowledgeReturns(AlertAcknowledgementOutcome.Acknowledged);

        var result = await CreateController().AcknowledgeAsLinkedMember(
            linkId, Request(null), CancellationToken.None);

        var response = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ChatAcknowledgeResponse>().Subject;
        response.AcknowledgedBy.Should().Be("Sam Tester");
        response.AlreadyAcknowledged.Should().BeFalse();
    }

    [Fact]
    public async Task NoExcursion_AppliesTheDecisionToEveryActiveExcursion_AndReportsAMute()
    {
        var linkId = InsertLink(_tenantId);
        var first = InsertExcursion();
        var second = InsertExcursion();
        var ended = InsertExcursion(endedAt: DateTime.UtcNow.AddMinutes(-1));
        _ackService
            .Setup(s => s.AcknowledgeExcursionAsync(
                It.IsAny<Guid>(), first, It.IsAny<string>(),
                It.IsAny<AlertAcknowledgementAuthority>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AlertAcknowledgementOutcome.Acknowledged);

        var result = await CreateController().AcknowledgeAsLinkedMember(
            linkId, Request(null), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ChatAcknowledgeResponse>()
            .Which.Outcome.Should().Be(AlertAcknowledgementOutcome.Muted);
        VerifyJudgedOnLinkedMember(first);
        VerifyJudgedOnLinkedMember(second);
        _ackService.Verify(s => s.AcknowledgeExcursionAsync(
            It.IsAny<Guid>(), ended, It.IsAny<string>(),
            It.IsAny<AlertAcknowledgementAuthority>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NoActiveExcursion_ReportsClosed()
    {
        var linkId = InsertLink(_tenantId);

        var result = await CreateController().AcknowledgeAsLinkedMember(
            linkId, Request(null), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ChatAcknowledgeResponse>()
            .Which.Outcome.Should().Be(AlertAcknowledgementOutcome.Closed);
    }

    [Fact]
    public async Task ChatAccountNotOnTheLink_IsForbidden()
    {
        var linkId = InsertLink(_tenantId);
        var excursionId = InsertExcursion();

        var result = await CreateController().AcknowledgeAsLinkedMember(
            linkId, Request(excursionId, platformUserId: "chat-user-2"), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        VerifyNothingAcknowledged();
    }

    [Fact]
    public async Task RevokedLink_IsNotFound()
    {
        var linkId = InsertLink(_tenantId, isActive: false);
        var excursionId = InsertExcursion();

        var result = await CreateController().AcknowledgeAsLinkedMember(
            linkId, Request(excursionId), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        VerifyNothingAcknowledged();
    }

    [Fact]
    public async Task LinkToAnotherTenantThanTheRequest_IsNotFound()
    {
        var linkId = InsertLink(NewTenant());
        var excursionId = InsertExcursion();

        var result = await CreateController().AcknowledgeAsLinkedMember(
            linkId, Request(excursionId), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        VerifyNothingAcknowledged();
    }

    [Fact]
    public async Task UnknownExcursion_IsNotFound()
    {
        var linkId = InsertLink(_tenantId);

        var result = await CreateController().AcknowledgeAsLinkedMember(
            linkId, Request(Guid.CreateVersion7()), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        VerifyNothingAcknowledged();
    }
}
