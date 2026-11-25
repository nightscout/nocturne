using Nocturne.API.Tests.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// The members list says whether each member has a confirmed recovery email: only where mail can
/// be sent, only for a person, only to a caller who manages members, and never as an address.
/// </summary>
[Trait("Category", "Unit")]
public class MemberListRecoveryEmailTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static TenantMemberRecord Member(
        string name, bool confirmed, bool system = false, bool demo = false) => new(
        Id: Guid.CreateVersion7(), TenantId: TenantId, SubjectId: Guid.CreateVersion7(), SubjectName: name,
        SubjectUsername: null, SubjectIsActive: true, IsSystemSubject: system, IsDemoSubject: demo,
        IsPlatformAdmin: false, HasConfirmedEmail: confirmed, Roles: [], DirectPermissions: null, Label: null,
        Username: null, LimitTo24Hours: false, LastUsedAt: null, CreatedFromInviteId: null, SysCreatedAt: DateTime.UtcNow,
        AccessEndsAt: null, Reasons: []);

    private static TenantService Service(bool deliveryConfigured, params TenantMemberRecord[] members)
    {
        var tenants = new Mock<ITenantRepository>();
        tenants.Setup(t => t.GetAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(new TenantRecord(
            Id: TenantId, Slug: "home", DisplayName: "Home", IsActive: true, IsDemo: false, AllowAccessRequests: true,
            AllowPublicDocs: false, OnboardingCompletedAt: null, SetupHubEnrolledAt: null, SetupStripDismissedRevision: null,
            PatientRelationship: null, HandedOverAt: null, DefaultGlucoseUnits: null, LastReadingAt: null,
            ShareTokenHash: null, ShareTokenEncrypted: null, ShareTokenSetAt: null, ShareLastAccessedAt: null,
            SysCreatedAt: DateTime.UtcNow, SysUpdatedAt: DateTime.UtcNow, DemoConfig: null));
        var membership = new Mock<ITenantMembershipRepository>();
        membership.Setup(m => m.ListMembersAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(members);

        return new TenantService(
            Mock.Of<Nocturne.Infrastructure.Data.Services.INocturneDbContextFactory>(),
            tenants.Object,
            membership.Object,
            Mock.Of<ISubjectRepository>(),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new OperatorConfiguration()),
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<ITenantRoleService>(),
            grantRevocation: null!,
            TimeProvider.System,
            NullLogger<TenantService>.Instance,
            AccountLinkRoutes.Of(deliveryConfigured), InertTeamStaff.Announcer());
    }

    private static async Task<Dictionary<string, bool?>> FlagsAsync(TenantService service) =>
        (await service.GetByIdAsync(TenantId))!.Members.ToDictionary(m => m.Name!, m => m.HasConfirmedRecoveryEmail);

    [Fact]
    public async Task APersonIsConfirmedOrNotAndASystemOrDemoSubjectIsNeither()
    {
        var service = Service(
            deliveryConfigured: true,
            Member("confirmed", confirmed: true),
            Member("unconfirmed", confirmed: false),
            Member("system", confirmed: false, system: true),
            Member("demo", confirmed: false, demo: true));

        (await FlagsAsync(service)).Should().Equal(new Dictionary<string, bool?>
        {
            ["confirmed"] = true,
            ["unconfirmed"] = false,
            ["system"] = null,
            ["demo"] = null,
        });
    }

    [Fact]
    public async Task WithNoDeliveryRouteNoMemberIsReportedEitherWay()
    {
        var service = Service(deliveryConfigured: false, Member("confirmed", true), Member("unconfirmed", false));

        (await FlagsAsync(service)).Values.Should().OnlyContain(v => v == null);
    }

    [Fact]
    public void TheDtoCarriesTheFlagAndNoAddress()
    {
        typeof(TenantMemberDto).GetProperties().Select(p => p.Name)
            .Where(n => n.Contains("Email", StringComparison.OrdinalIgnoreCase))
            .Should().Equal(nameof(TenantMemberDto.HasConfirmedRecoveryEmail));
    }

    private static MemberInviteController Controller(ITenantService tenantService, params string[] grantedScopes)
    {
        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(t => t.TenantId).Returns(TenantId);
        var http = new DefaultHttpContext();
        http.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        http.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = Guid.NewGuid(), TenantId = TenantId };
        return new MemberInviteController(
            Mock.Of<IMemberInviteService>(), tenantService, Mock.Of<ITenantRoleService>(),
            Mock.Of<ITenantMemberService>(), accessor.Object, Mock.Of<ITenantMembershipRepository>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static ITenantService ListOf(params bool?[] flags)
    {
        var detail = new TenantDetailDto(
            TenantId, "home", "Home", true, DateTime.UtcNow,
            flags.Select((flag, i) => new TenantMemberDto(
                Guid.CreateVersion7(), Guid.CreateVersion7(), $"m{i}", false, [], null, null, false, null, DateTime.UtcNow,
                false, null, false, flag, true, [], false)).ToList());
        var service = new Mock<ITenantService>();
        service.Setup(s => s.GetByIdAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(detail);
        return service.Object;
    }

    private static async Task<List<bool?>> ReadAsync(MemberInviteController controller) =>
        ((List<TenantMemberDto>)(await controller.GetMembers(default)).Should().BeOfType<OkObjectResult>().Subject.Value!)
            .Select(m => m.HasConfirmedRecoveryEmail).ToList();

    [Theory]
    [InlineData(Scope.MembersManage)]
    [InlineData(Scope.FullAccess)]
    public async Task ACallerWhoManagesMembersSeesTheFlag(string scope)
    {
        (await ReadAsync(Controller(ListOf(true, false), scope))).Should().Equal(true, false);
    }

    [Theory]
    [InlineData(Scope.MembersInvite)]
    [InlineData(Scope.SharingManage)]
    public async Task ACallerWhoOnlyInvitesOrSharesDoesNotSeeTheFlag(string scope)
    {
        (await ReadAsync(Controller(ListOf(true, false), scope))).Should().OnlyContain(v => v == null);
    }
}
