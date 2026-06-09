using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Round-trips the <c>platform_access</c> marker and tenant pin through
/// <see cref="JwtService"/> generation and validation.
/// </summary>
public class JwtServicePlatformAccessTests
{
    private readonly JwtService _jwt = new(
        Options.Create(new JwtOptions
        {
            SecretKey = "platform-access-test-secret-key-32+chars",
            Issuer = "nocturne",
            Audience = "nocturne-api",
        }),
        NullLogger<JwtService>.Instance);

    private readonly SubjectInfo _subject = new()
    {
        Id = Guid.CreateVersion7(),
        Name = "Operator",
        Email = "ops@example.com",
    };

    [Fact]
    public void PlatformAccessGrant_RoundTrips_MarkerAndTenantPin()
    {
        var tenantId = Guid.CreateVersion7();

        var token = _jwt.GenerateAccessToken(
            _subject, ["*"], [], [], tenantId: tenantId, platformAccess: true);

        var result = _jwt.ValidateAccessToken(token);

        result.IsValid.Should().BeTrue();
        result.Claims!.PlatformAccess.Should().BeTrue();
        result.Claims.TenantId.Should().Be(tenantId);
        result.Claims.SubjectId.Should().Be(_subject.Id);
    }

    [Fact]
    public void OrdinaryTenantPinnedToken_HasNoPlatformAccessMarker()
    {
        var token = _jwt.GenerateAccessToken(
            _subject, ["glucose.read"], [], [], tenantId: Guid.CreateVersion7(), platformAccess: false);

        var result = _jwt.ValidateAccessToken(token);

        result.IsValid.Should().BeTrue();
        result.Claims!.PlatformAccess.Should().BeFalse();
    }
}
