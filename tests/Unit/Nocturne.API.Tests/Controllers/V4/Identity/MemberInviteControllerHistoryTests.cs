using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

[Trait("Category", "Unit")]
public class MemberInviteControllerHistoryTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Mock<IMemberHistoryReader> _reader = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(Scope.GlucoseRead)]
    [InlineData(Scope.MembersInvite)]
    [InlineData(Scope.SharingManage)]
    public void GetMemberHistory_RefusesACallerWithoutMembersManage(string scope)
    {
        Authorize(new HashSet<string> { scope }).Should().BeOfType<ScopeForbiddenResult>();
    }

    [Theory]
    [InlineData(Scope.MembersManage)]
    [InlineData(Scope.FullAccess)]
    public void GetMemberHistory_AdmitsACallerWhoManagesMembers(string scope)
    {
        Authorize(new HashSet<string> { scope }).Should().BeNull();
    }

    [Theory]
    [InlineData(0, -5, 1, 0)]
    [InlineData(25, 0, 25, 0)]
    [InlineData(100, 50, 100, 50)]
    [InlineData(5000, 3, 100, 3)]
    public async Task GetMemberHistory_ClampsTheLimitAndTheOffset(int limit, int offset, int expectedLimit, int expectedOffset)
    {
        Arrange();

        await Controller().GetMemberHistory(_reader.Object, _time, null, limit, offset);

        _reader.Verify(r => r.GetAsync(_tenantId, null, expectedLimit, expectedOffset, _time.GetUtcNow().UtcDateTime, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetMemberHistory_ReadsTheResolvedTenantAndNarrowsToTheMemberAsked()
    {
        Arrange();
        var member = Guid.NewGuid();

        var result = await Controller().GetMemberHistory(_reader.Object, _time, member);

        result.Result.Should().BeOfType<OkObjectResult>();
        _reader.Verify(r => r.GetAsync(_tenantId, member, 25, 0, _time.GetUtcNow().UtcDateTime, It.IsAny<CancellationToken>()), Times.Once);
        _reader.VerifyNoOtherCalls();
    }

    private void Arrange() =>
        _reader
            .Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemberHistoryPage());

    private MemberInviteController Controller()
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.SetupGet(t => t.TenantId).Returns(_tenantId);
        return new MemberInviteController(
            Mock.Of<IMemberInviteService>(), Mock.Of<ITenantService>(), Mock.Of<ITenantRoleService>(),
            Mock.Of<ITenantMemberService>(), tenant.Object, Mock.Of<ITenantMembershipRepository>());
    }

    private static IActionResult? Authorize(IReadOnlySet<string> grantedScopes) =>
        ActionGate.Evaluate(
            typeof(MemberInviteController),
            nameof(MemberInviteController.GetMemberHistory),
            grantedScopes,
            new AuthContext { IsAuthenticated = true });
}
