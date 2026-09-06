using Fido2NetLib;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Extensions;

/// <summary>
/// The WebAuthn relying party derived at registration time from <c>BASE_DOMAIN</c>.
/// </summary>
public class PasskeyRelyingPartyRegistrationTests
{
    private static Fido2Configuration Resolve(string? baseDomain)
    {
        var settings = new Dictionary<string, string?>
        {
            [BaseDomainOptions.ConfigKey] = baseDomain,
            [$"{JwtOptions.SectionName}:SecretKey"] = new string('k', 64),
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthenticationAndIdentity(configuration);

        return services.BuildServiceProvider().GetRequiredService<IOptions<Fido2Configuration>>().Value;
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankBaseDomain_FallsBackToLocalhostRatherThanThrowing(string? baseDomain)
    {
        // A blank value reached Fido2Configuration.Origins as "https://", which throws
        // UriFormatException and takes every passkey endpoint down with it.
        var config = Resolve(baseDomain);

        config.ServerDomain.Should().Be("localhost");
        config.Origins.Should().Contain("https://localhost:1612");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConfiguredBaseDomain_BecomesTheRelyingPartyId()
    {
        var config = Resolve("cgm.example.com");

        config.ServerDomain.Should().Be("cgm.example.com");
        config.Origins.Should().Contain("https://cgm.example.com");
    }
}
