using FluentAssertions;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Unlinking an OIDC identity drops a primary sign-in factor, and the count of those is reported by
/// <see cref="PasskeyController.ListCredentials"/> — not by this controller's own read. The account
/// page decides from that count whether a passkey's Remove is offered at all, so a refresh list
/// naming only <see cref="OidcController.GetLinkedIdentities"/> leaves the count stale until the next
/// full load and the page keeps offering a Remove the server answers 409 <c>last_factor</c>.
/// </summary>
[Trait("Category", "Unit")]
public class OidcInvalidationTests
{
    [Theory]
    [InlineData(nameof(OidcController.GetLinkedIdentities))]
    [InlineData("Passkey_ListCredentials")]
    public void UnlinkingAnIdentity_RefreshesTheReadsThatReportIt(string read)
    {
        RemoteAttributes.Command<OidcController>(nameof(OidcController.UnlinkIdentity))
            .Invalidates.Should()
            .Contain(read);
    }
}
