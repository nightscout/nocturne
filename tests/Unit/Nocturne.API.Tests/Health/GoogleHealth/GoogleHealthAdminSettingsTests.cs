using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Shared.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Health.GoogleHealth;

public sealed class GoogleHealthAdminSettingsTests : IDisposable
{
    private const string Category = "google-health";
    private readonly NocturneDbContext db = new(new DbContextOptionsBuilder<NocturneDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly IOptions<BaseDomainOptions> domain = Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example" });
    private readonly SecretEncryptionService encryption = new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["INSTANCE_KEY"] = "test-instance-key" }).Build(), NullLogger<SecretEncryptionService>.Instance);

    private PlatformSettingsService Service() => new(new TestNocturneDbContextFactory(db), encryption, domain);
    private GoogleHealthPlatformSettings Runtime() => new(Service(), domain);
    private static Dictionary<string, string> Fields(string id = "test.apps.googleusercontent.com") => new()
    { ["clientId"] = id, ["clientSecret"] = "test-client-secret" };

    [Fact]
    public async Task Admin_save_encrypts_credentials_and_never_exposes_them_in_summaries_or_bot_bootstrap()
    {
        var service = Service();
        Assert.True((await service.UpsertAsync(Category, true, Fields())).Success);
        var stored = await db.PlatformSettings.SingleAsync();
        Assert.DoesNotContain("test-client-secret", stored.EncryptedJson);
        Assert.DoesNotContain("test.apps.googleusercontent.com", stored.EncryptedJson);
        var summary = await service.GetAsync(Category);
        Assert.Contains("clientSecret", summary!.ConfiguredFields);
        Assert.DoesNotContain("test-client-secret", JsonSerializer.Serialize(summary));
        Assert.DoesNotContain(await service.GetAllDecryptedAsync(), credentials => credentials.Category == Category);
        var runtime = await Runtime().GetAsync();
        Assert.True(runtime.IsConfigured);
        Assert.Equal("test-client-secret", runtime.ClientSecret);
        Assert.Equal("https://nocturne.example" + GoogleHealthPlatformOptions.CallbackPath, runtime.RedirectUri);
    }

    [Fact]
    public async Task Blank_fields_keep_secrets_and_new_requests_observe_updates_disable_and_delete_without_restart()
    {
        var service = Service();
        Assert.False((await Runtime().GetAsync()).IsConfigured);
        Assert.True((await service.UpsertAsync(Category, true, Fields())).Success);
        var inFlight = Runtime();
        Assert.Equal("test.apps.googleusercontent.com", (await inFlight.GetAsync()).ClientId);
        Assert.True((await service.UpsertAsync(Category, true, new()
        { ["clientId"] = "replacement.apps.googleusercontent.com", ["clientSecret"] = "" })).Success);
        Assert.Equal("test.apps.googleusercontent.com", (await inFlight.GetAsync()).ClientId);
        var next = await Runtime().GetAsync();
        Assert.Equal("replacement.apps.googleusercontent.com", next.ClientId);
        Assert.Equal("test-client-secret", next.ClientSecret);
        Assert.True((await service.UpsertAsync(Category, false, new())).Success);
        Assert.False((await Runtime().GetAsync()).IsConfigured);
        Assert.True(await service.DeleteAsync(Category));
        Assert.False((await Runtime().GetAsync()).IsConfigured);
    }

    [Theory]
    [InlineData("clientId", "invalid")]
    [InlineData("redirectUri", "http://example.com/api/v4/google-health/oauth/callback")]
    [InlineData("redirectUri", "https://example.com/wrong-path")]
    [InlineData("redirectUri", "https://example.com/api/v4/google-health/oauth/callback?x=y")]
    public async Task Invalid_oauth_configuration_is_rejected_without_persisting(string field, string value)
    {
        var fields = Fields();
        fields[field] = value;
        var result = await Service().UpsertAsync(Category, true, fields);
        Assert.False(result.Success);
        Assert.Contains(field, result.Errors!.Keys);
        Assert.Empty(await db.PlatformSettings.ToListAsync());
    }

    [Fact]
    public async Task Enabled_configuration_requires_both_client_fields_and_a_public_callback()
    {
        var noDomain = new PlatformSettingsService(new TestNocturneDbContextFactory(db), encryption,
            Options.Create(new BaseDomainOptions()));
        var result = await noDomain.UpsertAsync(Category, true, new());
        Assert.False(result.Success);
        Assert.Equal(3, result.Errors!.Count);
    }

    public void Dispose() => db.Dispose();
}
