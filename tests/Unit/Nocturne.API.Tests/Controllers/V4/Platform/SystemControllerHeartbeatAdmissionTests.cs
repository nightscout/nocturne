using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// The heartbeat sets the instance-wide chat-channel availability every tenant reads, so only the
/// web app's bot process reports it, with the instance key. No tenant credential may, however wide.
/// </summary>
public sealed class SystemControllerHeartbeatAdmissionTests
{
    private static readonly HashSet<string> Everything = [Scope.FullAccess];

    [Theory]
    [InlineData(AuthType.SessionCookie)]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.OAuthAccessToken)]
    public void A_tenant_credential_is_refused(AuthType credential) =>
        ActionGate.Evaluate(
                typeof(SystemController),
                nameof(SystemController.Heartbeat),
                Everything,
                new AuthContext { IsAuthenticated = true, AuthType = credential })
            .Should().BeOfType<ForbidResult>();

    [Fact]
    public void The_instance_key_reaches_the_handler() =>
        ActionGate.Evaluate(
                typeof(SystemController),
                nameof(SystemController.Heartbeat),
                Everything,
                new AuthContext { IsAuthenticated = true, AuthType = AuthType.InstanceKey })
            .Should().BeNull();
}
