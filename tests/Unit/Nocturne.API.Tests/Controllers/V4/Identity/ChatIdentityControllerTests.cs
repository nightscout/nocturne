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
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// Covers what the tenant-scoped link list tells a member about a chat account whose default sits
/// on a link in a tenant this list does not show.
/// </summary>
[Trait("Category", "Unit")]
public class ChatIdentityControllerTests
{
    private const string Platform = "discord";
    private const string ChatUser = "discord-user-a";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly Guid _callerSubjectId = Guid.NewGuid();
    private readonly DbContextOptions<NocturneDbContext> _dbOptions =
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

    private NocturneDbContext CreateDbContext() => new(_dbOptions);

    private void InsertLink(Guid tenantId, Guid subjectId, string label, bool isDefault)
    {
        using var db = CreateDbContext();
        db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
        {
            Id = Guid.CreateVersion7(),
            Platform = Platform,
            PlatformUserId = ChatUser,
            TenantId = tenantId,
            NocturneUserId = subjectId,
            Label = label,
            DisplayName = label,
            IsDefault = isDefault,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private ChatIdentityController CreateController(bool withSubject = true)
    {
        var factory = new Mock<IDbContextFactory<NocturneDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateDbContext);

        var service = new ChatIdentityService(
            new ChatIdentityDirectoryService(
                factory.Object, Mock.Of<ILogger<ChatIdentityDirectoryService>>()),
            new ChatIdentityPendingLinkService(
                factory.Object, Mock.Of<ILogger<ChatIdentityPendingLinkService>>()),
            factory.Object,
            Mock.Of<ILogger<ChatIdentityService>>());

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(_tenantId);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = withSubject ? _callerSubjectId : null,
            TenantId = _tenantId,
        };

        return new ChatIdentityController(service, tenantAccessor.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

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
        InsertLink(_tenantId, Guid.NewGuid(), "lily", isDefault: false);
        InsertLink(_otherTenantId, Guid.NewGuid(), "oliver", isDefault: true);

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
}
