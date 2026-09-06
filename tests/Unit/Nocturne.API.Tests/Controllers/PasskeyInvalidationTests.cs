using System.Reflection;
using FluentAssertions;
using Nocturne.API.Controllers.Authentication;
using OpenApi.Remote.Attributes;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Minting recovery codes gives an account a second way in, which
/// <see cref="PasskeyCredentialListResponse.HasSingleSignInMethod"/> reports from a read of its
/// own — a hint that lists only the recovery status leaves the backup-sign-in prompt on screen
/// until the next full load.
/// </summary>
[Trait("Category", "Unit")]
public class PasskeyInvalidationTests
{
    [Theory]
    [InlineData(nameof(PasskeyController.GetRecoveryStatus))]
    [InlineData(nameof(PasskeyController.ListCredentials))]
    public void RegeneratingRecoveryCodes_RefreshesTheReadsThatReportThem(string read)
    {
        Command(nameof(PasskeyController.RegenerateRecoveryCodes))
            .Invalidates.Should()
            .Contain(read);
    }

    [Theory]
    [InlineData(nameof(PasskeyController.GetRecoveryStatus))]
    [InlineData(nameof(PasskeyController.ListCredentials))]
    public void EveryRefreshedRead_IsAQueryTheCommandCanRefresh(string read)
    {
        typeof(PasskeyController).GetMethod(read)!
            .GetCustomAttribute<RemoteQueryAttribute>().Should().NotBeNull();
    }

    private static RemoteCommandAttribute Command(string write) =>
        typeof(PasskeyController).GetMethod(write)!
            .GetCustomAttribute<RemoteCommandAttribute>()!;
}
