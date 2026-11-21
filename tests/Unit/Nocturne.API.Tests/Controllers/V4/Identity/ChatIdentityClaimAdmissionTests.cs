using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// Claiming a chat link binds a chat account to the caller's own subject, so only the person's own
/// sign-in may claim. A delegated credential is refused whatever scopes it holds.
/// </summary>
public sealed class ChatIdentityClaimAdmissionTests
{
    private static readonly HashSet<string> Everything = [Scope.FullAccess];

    [Theory]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.InstanceKey)]
    public void A_delegated_credential_is_refused(AuthType credential) =>
        ActionGate.Evaluate(
                typeof(ChatIdentityController),
                nameof(ChatIdentityController.ClaimLink),
                Everything,
                new AuthContext { IsAuthenticated = true, AuthType = credential })
            .Should().BeOfType<ForbidResult>();

    [Fact]
    public void The_members_own_sign_in_reaches_the_handler() =>
        ActionGate.Evaluate(
                typeof(ChatIdentityController),
                nameof(ChatIdentityController.ClaimLink),
                new HashSet<string> { Scope.GlucoseRead },
                new AuthContext { IsAuthenticated = true, AuthType = AuthType.SessionCookie })
            .Should().BeNull();
}
