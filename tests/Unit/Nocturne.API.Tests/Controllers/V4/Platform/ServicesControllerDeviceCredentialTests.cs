using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// The connections list says whether the caller may keep a device credential past its stop: only a
/// caller who manages the members and whose own access does not end. A connector that adopts the
/// secret a caller saves tells that caller when it would stop.
/// </summary>
[Trait("Category", "Unit")]
public class ServicesControllerDeviceCredentialTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Caller = Guid.CreateVersion7();
    private static readonly DateTime CallerEnd = new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IConnectionService> _connections = new();
    private AuthType _authType = AuthType.SessionCookie;

    private ServicesController Controller(DateTime? callerAccessEndsAt, params string[] scopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = _authType,
            SubjectId = Caller,
            TenantId = Tenant,
        };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)scopes.ToHashSet();
        new RequestMembership(
                Caller, Tenant,
                new MembershipFacts(Guid.CreateVersion7(), new TenantMemberAccess(scopes.ToHashSet(), false, callerAccessEndsAt), new TenantMemberAccess(scopes.ToHashSet(), false, callerAccessEndsAt), null))
            .StoreOn(httpContext);

        var dataSources = new Mock<IDataSourceService>();
        dataSources
            .Setup(d => d.GetConnectorCapabilities(It.IsAny<string>()))
            .Returns(() => new ConnectorCapabilities());

        return new ServicesController(
            dataSources.Object,
            _connections.Object,
            Mock.Of<IConnectorHealthService>(),
            Mock.Of<IConnectorSyncService>(),
            Mock.Of<ILogger<ServicesController>>(),
            Mock.Of<ITenantAccessor>(),
            Options.Create(new BaseDomainOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TheListSaysWhetherTheCallerMayKeep(bool callerAccessEnds, bool mayKeep)
    {
        _connections
            .Setup(c => c.GetConnectionsAsync(Caller, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectionsOverview());

        await Controller(callerAccessEnds ? CallerEnd : null, Scope.MembersManage, Scope.DevicesRead).GetConnections();

        _connections.Verify(c => c.GetConnectionsAsync(Caller, mayKeep, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ACallerWhoDoesNotManageMembersMayNotKeep()
    {
        _connections
            .Setup(c => c.GetConnectionsAsync(Caller, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectionsOverview());

        await Controller(null, Scope.DevicesRead).GetConnections();

        _connections.Verify(c => c.GetConnectionsAsync(Caller, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ACallerThatIsNotAnInteractiveSessionMayNotKeep()
    {
        _authType = AuthType.ApiKey;
        _connections
            .Setup(c => c.GetConnectionsAsync(Caller, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectionsOverview());

        await Controller(null, Scope.MembersManage, Scope.DevicesRead).GetConnections();

        _connections.Verify(c => c.GetConnectionsAsync(Caller, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("nightscout", true, true)]
    [InlineData("nightscout", false, false)]
    [InlineData("dexcom", true, false)]
    public void TheConnectorSaysWhenASavedSecretWouldStop(string connector, bool callerAccessEnds, bool stops)
    {
        var capabilities = Capabilities(Controller(callerAccessEnds ? CallerEnd : null, Scope.TenantSettings), connector);

        capabilities.SavedSecretStopsAt.Should().Be(stops ? CallerEnd : null);
        capabilities.SavedSecretMakesNoCredential.Should().BeFalse();
    }

    [Theory]
    [InlineData("nightscout", true)]
    [InlineData("dexcom", false)]
    public void ACallerWhoOnlySetsUpDataSourcesIsToldASavedSecretMakesNoCredential(string connector, bool makesNone)
    {
        var capabilities = Capabilities(Controller(CallerEnd, Scope.ConnectorsReadWrite), connector);

        capabilities.SavedSecretMakesNoCredential.Should().Be(makesNone);
        capabilities.SavedSecretStopsAt.Should().BeNull("a caller who makes no credential has none to stop");
    }

    public static TheoryData<GrantCeiling?> CeilingsThatMayNotKeep => new()
    {
        new GrantCeiling(new HashSet<string> { Scope.MembersManage }, CallerEnd, false),
        new GrantCeiling(new HashSet<string> { Scope.DevicesRead }, null, false),
        null,
    };

    [Theory]
    [MemberData(nameof(CeilingsThatMayNotKeep))]
    public async Task AMixedOrTeamOnlyMemberMayKeepOnlyWhatTheDirectReasonAllows(GrantCeiling? ceiling)
    {
        _connections
            .Setup(c => c.GetConnectionsAsync(Caller, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectionsOverview());
        var controller = Controller(null, Scope.MembersManage, Scope.DevicesRead);
        controller.HttpContext.SetGrantCeiling(ceiling);

        await controller.GetConnections();

        _connections.Verify(c => c.GetConnectionsAsync(Caller, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    public static TheoryData<GrantCeiling?> CeilingsThatMayNotAdopt => new()
    {
        new GrantCeiling(new HashSet<string> { Scope.TenantSettings }, null, true),
        new GrantCeiling(new HashSet<string> { Scope.ConnectorsReadWrite }, null, false),
        null,
    };

    [Theory]
    [MemberData(nameof(CeilingsThatMayNotAdopt))]
    public void AMixedOrTeamOnlyMemberIsToldASavedSecretMakesNoCredentialUnlessTheDirectReasonMayMakeOne(GrantCeiling? ceiling)
    {
        var controller = Controller(null, Scope.TenantSettings);
        controller.HttpContext.SetGrantCeiling(ceiling);

        Capabilities(controller, "nightscout").SavedSecretMakesNoCredential.Should().BeTrue();
    }

    [Fact]
    public void ASavedSecretStopsWithTheSumNotTheDirectReason()
    {
        var controller = Controller(CallerEnd, Scope.TenantSettings);
        controller.HttpContext.SetGrantCeiling(new GrantCeiling(new HashSet<string> { Scope.TenantSettings }, null, false));

        Capabilities(controller, "nightscout").SavedSecretStopsAt.Should().Be(CallerEnd);
    }

    private static ConnectorCapabilities Capabilities(ServicesController controller, string connector) =>
        controller.GetConnectorCapabilities(connector)
            .Result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<ConnectorCapabilities>().Subject;
}
