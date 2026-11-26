using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.PlatformAdmin;

[Trait("Category", "Unit")]
public class TenantUsageControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly Mock<ITenantUsageReader> _reader = new();

    [Fact]
    public void IsGatedOnThePlatformAdminRole()
    {
        typeof(TenantUsageController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single()
            .Roles.Should().Be("platform_admin");
    }

    [Fact]
    public async Task TheInstanceKeyGetsThePageItAskedFor()
    {
        var tenantId = Guid.NewGuid();
        var page = new List<TenantUsage> { Usage(tenantId) };
        _reader.Setup(r => r.ReadAsync(tenantId, 200, 50, It.IsAny<Func<string, TimeSpan>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

        var result = await Controller(InstanceKey()).Get(tenantId, offset: 200, limit: 50);

        var summary = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<TenantUsageSummary>().Subject;
        summary.GeneratedAt.Should().Be(Now.UtcDateTime);
        summary.Tenants.Should().BeSameAs(page);
    }

    [Fact]
    public async Task AConnectorIsStaleAfterTheThresholdItsMetadataDeclares()
    {
        Func<string, TimeSpan>? staleAfter = null;
        _reader.Setup(r => r.ReadAsync(null, 0, TenantUsageController.DefaultLimit, It.IsAny<Func<string, TimeSpan>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid?, int, int, Func<string, TimeSpan>, CancellationToken>((_, _, _, f, _) => staleAfter = f)
            .ReturnsAsync([]);

        await Controller(InstanceKey()).Get(null);

        staleAfter!("glooko").Should().Be(TimeSpan.FromHours(6));
        staleAfter("not-a-connector").Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task APlatformAdminPersonIsRefused()
    {
        var person = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = Guid.NewGuid(),
            IsPlatformAdmin = true,
        };

        var result = await Controller(person).Get(null);

        result.Should().BeOfType<ForbidResult>();
        _reader.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 0)]
    [InlineData(0, TenantUsageController.MaxLimit + 1)]
    public async Task APageOutOfRangeIsABadRequest(int offset, int limit)
    {
        var result = await Controller(InstanceKey()).Get(null, offset, limit);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _reader.VerifyNoOtherCalls();
    }

    private TenantUsageController Controller(AuthContext auth)
    {
        var http = new DefaultHttpContext();
        http.SetAuthContext(auth);
        return new TenantUsageController(_reader.Object, new FakeTimeProvider(Now))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static AuthContext InstanceKey() => new()
    {
        IsAuthenticated = true,
        AuthType = AuthType.InstanceKey,
        IsPlatformAdmin = true,
    };

    private static TenantUsage Usage(Guid tenantId) => new()
    {
        TenantId = tenantId,
        Slug = "site",
        IsActive = true,
        MemberCount = 1,
        MembersActiveLast7Days = 1,
        Connectors = [],
        AlertRuleCount = 0,
        AlertChannelTypes = [],
        SavedReportCount = 0,
        TrackerCount = 0,
        ClockFaceCount = 0,
        GuestLinkCount = 0,
        PublicShareEnabled = false,
        ClientDevices = [],
        ChatIdentityLinked = false,
        OauthClientCount = 0,
        CoachMarksSeenCount = 0,
    };
}
