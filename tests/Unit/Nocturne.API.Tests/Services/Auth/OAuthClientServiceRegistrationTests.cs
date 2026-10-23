using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Dynamic client registration: every call is a new client, and the verified identity follows the
/// directory's redirect URIs, never the registrant's.
/// </summary>
public class OAuthClientServiceRegistrationTests : IDisposable
{
    private const string HomeAssistant = "io.home-assistant.nocturne";
    private const string HomeAssistantRedirect = "https://my.home-assistant.io/redirect/oauth";
    private const string Trio = "org.nightscout.trio";
    private const string TrioCallback = "org.nightscout.trio://oauth/callback";

    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly Guid _tenantId = Guid.CreateVersion7();

    public OAuthClientServiceRegistrationTests()
    {
        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private NocturneDbContext NewContext() =>
        new(_options) { TenantId = _tenantId };

    private OAuthClientService CreateService(NocturneDbContext context) =>
        new(context.AsFactory(), new RedirectUriValidator(), NullLogger<OAuthClientService>.Instance);

    private static Task<OAuthClientInfo> Register(
        OAuthClientService service, string? softwareId, params string[] uris) =>
        service.RegisterClientAsync(
            softwareId, "Registrant", "https://registrant.example", null, uris, "glucose.read", "203.0.113.7");

    [Theory]
    [InlineData(HomeAssistant, HomeAssistantRedirect, false)]
    [InlineData(Trio, TrioCallback, true)]
    [InlineData("com.example.app", "com.example.app://oauth/callback", false)]
    public async Task Register_TheSameSoftwareIdAndRedirectUriTwice_MintsTwoClients(
        string softwareId, string redirectUri, bool known)
    {
        using var context = NewContext();
        var service = CreateService(context);

        var first = await Register(service, softwareId, redirectUri);
        var second = await Register(service, softwareId, redirectUri);

        second.ClientId.Should().NotBe(first.ClientId);
        second.Id.Should().NotBe(first.Id);
        first.IsKnown.Should().Be(known);
        second.IsKnown.Should().Be(known);
        var stored = await context.OAuthClients.AsNoTracking()
            .Where(c => c.SoftwareId == softwareId)
            .Select(c => c.ClientId)
            .ToListAsync();
        stored.Should().BeEquivalentTo([first.ClientId, second.ClientId]);
    }

    [Fact]
    public async Task Register_WithTheDirectoryCallback_GetsTheDirectoryIdentity()
    {
        using var context = NewContext();

        var info = await Register(CreateService(context), Trio, TrioCallback);

        info.IsKnown.Should().BeTrue();
        info.SoftwareId.Should().Be(Trio);
        info.DisplayName.Should().Be("Trio");
        info.ClientUri.Should().Be("https://triodocs.org");
        info.LogoUri.Should().Be("/logos/trio.svg");
    }

    [Fact]
    public async Task Register_WithAKnownSoftwareIdAndAForeignCallback_IsStoredUnverifiedUnderTheRegistrantsName()
    {
        using var context = NewContext();
        var uris = new[] { "https://elsewhere.example/callback" };

        var info = await Register(CreateService(context), Trio, uris);

        info.IsKnown.Should().BeFalse();
        info.SoftwareId.Should().Be(Trio);
        info.DisplayName.Should().Be("Registrant");
        info.ClientUri.Should().Be("https://registrant.example");
        info.LogoUri.Should().BeNull();
        info.RedirectUris.Should().BeEquivalentTo(uris);
        var stored = await context.OAuthClients.AsNoTracking().SingleAsync(c => c.ClientId == info.ClientId);
        stored.ClientName.Should().Be("Registrant");
        stored.CreatedFromIp.Should().Be("203.0.113.7");
    }

    [Fact]
    public async Task Register_WithTheDirectoryLoopbackCallbackOnAnotherPort_IsVerified()
    {
        using var context = NewContext();

        var info = await Register(CreateService(context), "com.nocturne.tray", "http://127.0.0.1:5555/callback");

        info.IsKnown.Should().BeTrue();
        info.DisplayName.Should().Be("Nocturne Tray");
    }

    [Fact]
    public async Task Register_ADirectoryAppWithoutRedirectUris_IsVerified()
    {
        using var context = NewContext();

        var info = await Register(CreateService(context), HomeAssistant);

        info.IsKnown.Should().BeTrue();
        info.DisplayName.Should().Be("Home Assistant");
    }

    [Fact]
    public async Task Register_AnUnknownSoftwareId_IsSelfRegisteredAndKeepsItsSoftwareId()
    {
        using var context = NewContext();

        var info = await Register(CreateService(context), "com.example.app", "com.example.app://oauth/callback");

        info.IsKnown.Should().BeFalse();
        info.SoftwareId.Should().Be("com.example.app");
        info.DisplayName.Should().Be("Registrant");
    }

    public void Dispose()
    {
        using var context = NewContext();
        context.Database.EnsureDeleted();
    }
}
