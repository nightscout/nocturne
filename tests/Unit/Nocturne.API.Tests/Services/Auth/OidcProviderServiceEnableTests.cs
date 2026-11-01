using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// <see cref="OidcProviderService.EnableProviderAsync"/> and
/// <see cref="OidcProviderService.DisableProviderAsync"/> set the provider's enabled flag through
/// the repository and report whether the provider exists.
/// </summary>
public class OidcProviderServiceEnableTests
{
    private readonly Mock<IOidcProviderRepository> _providers = new();

    private OidcProviderService BuildService() => new(
        _providers.Object,
        new Mock<IHttpClientFactory>().Object,
        NullLogger<OidcProviderService>.Instance,
        new EphemeralDataProtectionProvider(),
        Options.Create(new OidcOptions()));

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SetsTheFlagThroughTheRepository(bool enable, bool exists)
    {
        var id = Guid.NewGuid();
        _providers.Setup(p => p.SetEnabledAsync(id, enable, It.IsAny<CancellationToken>())).ReturnsAsync(exists);
        var service = BuildService();

        var result = enable ? await service.EnableProviderAsync(id) : await service.DisableProviderAsync(id);

        result.Should().Be(exists);
        _providers.Verify(p => p.SetEnabledAsync(id, enable, It.IsAny<CancellationToken>()), Times.Once);
        _providers.Verify(p => p.SetEnabledAsync(id, !enable, It.IsAny<CancellationToken>()), Times.Never);
    }
}
