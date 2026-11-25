using FluentAssertions;
using Nocturne.Connectors.Nightscout.Services;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services;

/// <summary>
/// The <c>api-secret</c> header Nightscout compares against the SHA-1 of its own secret, as lowercase hex.
/// </summary>
public class NightscoutApiSecretHashTests
{
    [Fact]
    public void A_plain_secret_is_sent_as_its_lowercase_sha1()
    {
        NightscoutConnectorService.ComputeApiSecretHash("secret")
            .Should().Be("e5e9fa1ba31ecd1ae84f75caaa474f3a663f05f4");
    }

    [Fact]
    public void A_secret_already_hashed_is_sent_lowercased_not_hashed_again()
    {
        NightscoutConnectorService.ComputeApiSecretHash("E5E9FA1BA31ECD1AE84F75CAAA474F3A663F05F4")
            .Should().Be("e5e9fa1ba31ecd1ae84f75caaa474f3a663f05f4");
    }
}
