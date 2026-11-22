using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4.Platform;
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
                new MembershipFacts(Guid.CreateVersion7(), new TenantMemberAccess(scopes.ToHashSet(), false, callerAccessEndsAt), null))
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
        var result = Controller(callerAccessEnds ? CallerEnd : null, Scope.ConnectorsReadWrite)
            .GetConnectorCapabilities(connector);

        var capabilities = result.Result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<ConnectorCapabilities>().Subject;
        capabilities.SavedSecretStopsAt.Should().Be(stops ? CallerEnd : null);
    }
}
