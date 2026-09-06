using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Regression tests for DCR against pre-seeded known-directory clients (#810 bug 1):
/// a seed ships with an empty RedirectUris list, so returning it unchanged silently
/// discarded the submitted redirect_uris and permanently broke /authorize for that client.
/// </summary>
public class OAuthClientServiceRegistrationTests : IDisposable
{
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly Guid _tenantId = Guid.CreateVersion7();

    public OAuthClientServiceRegistrationTests()
    {
        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = NewContext();
        context.OAuthClients.Add(new OAuthClientEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            ClientId = "seeded-client-id",
            SoftwareId = "io.home-assistant.nocturne",
            ClientName = "Home Assistant",
            DisplayName = "Home Assistant",
            IsKnown = true,
            // The known directory cannot know a per-instance callback URL.
            RedirectUris = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        context.SaveChanges();
    }

    private NocturneDbContext NewContext() =>
        new(_options) { TenantId = _tenantId };

    private OAuthClientService CreateService(NocturneDbContext context) =>
        new(context, new RedirectUriValidator(), NullLogger<OAuthClientService>.Instance);

    [Fact]
    public async Task Register_WhenSoftwareIdMatchesEmptyKnownSeed_AdoptsSubmittedRedirectUris()
    {
        using var context = NewContext();
        var service = CreateService(context);
        var uris = new[] { "http://127.0.0.1:8123/auth/nocturne/callback" };

        var info = await service.RegisterClientAsync(
            "io.home-assistant.nocturne",
            "Home Assistant",
            "https://www.home-assistant.io",
            null,
            uris,
            "glucose.read treatments.read",
            null);

        // The registration response must carry the submitted URIs so the client
        // can immediately proceed to /authorize.
        info.RedirectUris.Should().BeEquivalentTo(uris);

        var stored = await context.OAuthClients
            .SingleAsync(c => c.SoftwareId == "io.home-assistant.nocturne");
        stored.RedirectUris.Should().Contain("127.0.0.1:8123");
        stored.IsKnown.Should().BeTrue();
    }

    [Fact]
    public async Task Register_WhenSeedAlreadyCompletedRegistration_ReturnsItUnchanged()
    {
        using var context = NewContext();
        // Complete the seed first, exactly like the first real DCR would.
        var service = CreateService(context);
        await service.RegisterClientAsync(
            "io.home-assistant.nocturne",
            "Home Assistant",
            "https://www.home-assistant.io",
            null,
            new[] { "http://127.0.0.1:8123/auth/nocturne/callback" },
            "glucose.read",
            null);

        // A second registration must be fully idempotent: the original URIs survive.
        var second = await service.RegisterClientAsync(
            "io.home-assistant.nocturne",
            "Renamed Client",
            "https://example.com",
            null,
            new[] { "https://evil.example.com/callback" },
            "glucose.read",
            null);

        second.RedirectUris.Should().BeEquivalentTo(
            new[] { "http://127.0.0.1:8123/auth/nocturne/callback" });

        var stored = await context.OAuthClients
            .SingleAsync(c => c.SoftwareId == "io.home-assistant.nocturne");
        stored.RedirectUris.Should().NotContain("evil.example.com");
        stored.DisplayName.Should().Be("Home Assistant");
    }

    [Fact]
    public async Task Register_WithoutExistingRow_CreatesNewClientWithUris()
    {
        using var context = NewContext();
        var service = CreateService(context);
        var uris = new[] { "org.loopkit.loop://oauth/callback" };

        var info = await service.RegisterClientAsync(
            "org.loopkit.loop.newinstall", null, null, null, uris, "glucose.read", null);

        info.RedirectUris.Should().BeEquivalentTo(uris);
        info.IsKnown.Should().BeFalse();

        (await context.OAuthClients.CountAsync(c => c.SoftwareId == "org.loopkit.loop.newinstall"))
            .Should().Be(1);
    }

    public void Dispose()
    {
        using var context = NewContext();
        context.Database.EnsureDeleted();
    }
}
