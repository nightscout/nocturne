using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Dynamic client registration against the tenant's seeded known-directory clients: the verified
/// identity follows the directory's redirect URIs, never the registrant's.
/// </summary>
public class OAuthClientServiceRegistrationTests : IDisposable
{
    private const string HomeAssistant = "io.home-assistant.nocturne";
    private const string Trio = "org.nightscout.trio";
    private const string TrioCallback = "org.nightscout.trio://oauth/callback";

    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly Guid _tenantId = Guid.CreateVersion7();

    public OAuthClientServiceRegistrationTests()
    {
        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = NewContext();
        context.OAuthClients.AddRange(
            Seed("seeded-ha", HomeAssistant, "Home Assistant", "[]"),
            Seed("seeded-trio", Trio, "Trio", $"[\"{TrioCallback}\"]"),
            Seed("seeded-tray", "com.nocturne.tray", "Nocturne Tray", "[\"http://127.0.0.1/callback\"]"));
        context.SaveChanges();
    }

    private OAuthClientEntity Seed(string clientId, string softwareId, string name, string redirectUris) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = _tenantId,
        ClientId = clientId,
        SoftwareId = softwareId,
        ClientName = name,
        DisplayName = name,
        ClientUri = "https://directory.example/" + clientId,
        IsKnown = true,
        RedirectUris = redirectUris,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private NocturneDbContext NewContext() =>
        new(_options) { TenantId = _tenantId };

    private OAuthClientService CreateService(NocturneDbContext context) =>
        new(context.AsFactory(), new RedirectUriValidator(), NullLogger<OAuthClientService>.Instance);

    private static Task<OAuthClientInfo> Register(
        OAuthClientService service, string? softwareId, params string[] uris) =>
        service.RegisterClientAsync(
            softwareId, "Registrant", "https://registrant.example", null, uris, "glucose.read", null);

    [Fact]
    public async Task Register_WithACallbackTheSeedDoesNotList_GetsAnUnverifiedClientAndLeavesTheSeedAlone()
    {
        using var context = NewContext();
        var uris = new[] { "https://ha.example.net/auth/external/callback" };

        var info = await Register(CreateService(context), HomeAssistant, uris);

        info.ClientId.Should().NotBe("seeded-ha");
        info.IsKnown.Should().BeFalse();
        info.SoftwareId.Should().BeNull();
        info.DisplayName.Should().Be("Registrant");
        info.RedirectUris.Should().BeEquivalentTo(uris);

        var seed = await context.OAuthClients.AsNoTracking().SingleAsync(c => c.SoftwareId == HomeAssistant);
        seed.ClientId.Should().Be("seeded-ha");
        seed.RedirectUris.Should().Be("[]");
        seed.ClientUri.Should().Be("https://directory.example/seeded-ha");
        seed.IsKnown.Should().BeTrue();
    }

    [Fact]
    public async Task Register_WithoutRedirectUris_ReturnsTheSeed()
    {
        using var context = NewContext();

        var info = await Register(CreateService(context), HomeAssistant);

        info.ClientId.Should().Be("seeded-ha");
        info.IsKnown.Should().BeTrue();
    }

    [Fact]
    public async Task Register_WithTheDirectoryCallback_ReturnsTheVerifiedSeed()
    {
        using var context = NewContext();

        var info = await Register(CreateService(context), Trio, TrioCallback);

        info.ClientId.Should().Be("seeded-trio");
        info.IsKnown.Should().BeTrue();
    }

    [Fact]
    public async Task Register_WithTheDirectoryLoopbackCallbackOnAnotherPort_ReturnsTheVerifiedSeed()
    {
        using var context = NewContext();

        var info = await Register(CreateService(context), "com.nocturne.tray", "http://127.0.0.1:5555/callback");

        info.ClientId.Should().Be("seeded-tray");
        info.IsKnown.Should().BeTrue();
    }

    [Fact]
    public async Task Register_WithAKnownSoftwareIdAndAForeignCallback_GetsAnUnverifiedClient()
    {
        using var context = NewContext();
        var uris = new[] { "https://elsewhere.example/callback" };

        var info = await Register(CreateService(context), Trio, uris);

        info.ClientId.Should().NotBe("seeded-trio");
        info.IsKnown.Should().BeFalse();
        info.RedirectUris.Should().BeEquivalentTo(uris);
        (await context.OAuthClients.AsNoTracking().SingleAsync(c => c.SoftwareId == Trio))
            .RedirectUris.Should().Be($"[\"{TrioCallback}\"]");
    }

    [Fact]
    public async Task Register_AKnownSoftwareIdWithNoSeed_IsVerifiedOnlyWithTheDirectoryCallback()
    {
        using var context = NewContext();
        context.OAuthClients.RemoveRange(context.OAuthClients);
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var foreign = await Register(service, Trio, "https://elsewhere.example/callback");
        var genuine = await Register(service, Trio, TrioCallback);

        foreign.IsKnown.Should().BeFalse();
        foreign.SoftwareId.Should().BeNull();
        genuine.IsKnown.Should().BeTrue();
        genuine.SoftwareId.Should().Be(Trio);
        genuine.DisplayName.Should().Be("Trio");
        genuine.ClientUri.Should().Be("https://triodocs.org");
    }

    [Fact]
    public async Task Register_AnUnknownSoftwareIdTwiceWithTheSameUris_ReturnsTheSameClient()
    {
        using var context = NewContext();
        var service = CreateService(context);
        var uris = new[] { "com.example.app://oauth/callback" };

        var first = await Register(service, "com.example.app", uris);
        var second = await Register(service, "com.example.app", uris);

        first.IsKnown.Should().BeFalse();
        second.ClientId.Should().Be(first.ClientId);
    }

    [Fact]
    public async Task Register_AnUnknownSoftwareIdWithOtherUris_DoesNotReturnTheFirstRegistrantsClient()
    {
        using var context = NewContext();
        var service = CreateService(context);

        var first = await Register(service, "com.example.app", "com.example.app://oauth/callback");
        var second = await Register(service, "com.example.app", "https://elsewhere.example/callback");

        second.ClientId.Should().NotBe(first.ClientId);
        second.SoftwareId.Should().BeNull();
        second.RedirectUris.Should().BeEquivalentTo(["https://elsewhere.example/callback"]);
    }

    public void Dispose()
    {
        using var context = NewContext();
        context.Database.EnsureDeleted();
    }
}
