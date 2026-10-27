using FluentAssertions;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Tests.Infrastructure;
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
        RemoteAttributes.Command<PasskeyController>(nameof(PasskeyController.RegenerateRecoveryCodes))
            .Invalidates.Should()
            .Contain(read);
    }

    [Fact]
    public void RegisteringAPasskey_RefreshesTheCredentialList()
    {
        RemoteAttributes.Command<PasskeyController>(nameof(PasskeyController.RegisterComplete))
            .Invalidates.Should()
            .Contain(nameof(PasskeyController.ListCredentials));
    }

    [Theory]
    [InlineData(nameof(PasskeyController.GetRecoveryStatus))]
    [InlineData(nameof(PasskeyController.ListCredentials))]
    public void EveryRefreshedRead_IsAQueryTheCommandCanRefresh(string read)
    {
        RemoteAttributes.Query<PasskeyController>(read).Should().NotBeNull();
    }
}
