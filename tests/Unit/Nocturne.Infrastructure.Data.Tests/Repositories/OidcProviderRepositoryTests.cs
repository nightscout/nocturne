using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="OidcProviderRepository"/> over SQLite: the provider rows as stored, with their
/// secrets as ciphertext, and the config sync that makes the table match the configured set.
/// </summary>
public class OidcProviderRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Past = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;

    public OidcProviderRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(Guid.CreateVersion7());
    }

    public void Dispose() => _db.Dispose();

    private OidcProviderRepository Providers() => new(new TestNocturneDbContextFactory(_db.CreateContext(Guid.Empty)));

    private async Task AgeAsync(params Guid[] ids)
    {
        await using var db = _db.CreateContext(Guid.Empty);
        await db.OidcProviders.Where(p => ids.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, Past));
    }

    private static NewOidcProvider Keycloak(Guid? id = null, bool enabled = true, int order = 0) => new(
        "Keycloak", "oidc", "https://issuer.example", "nocturne", Id: id, IsEnabled: enabled, DisplayOrder: order,
        ClientSecretEncrypted: [1, 2, 3], Scopes: ["openid", "email"], ClaimMappingsJson: "{\"a\":\"b\"}", DefaultRoles: ["readable"]);

    [Fact]
    public async Task ACreatedProviderReadsBackAsStoredAndIsFoundByIssuerWithOrWithoutASlash()
    {
        var wanted = Guid.CreateVersion7();
        var id = await Providers().CreateAsync(Keycloak(wanted), CancellationToken.None);

        id.Should().Be(wanted);
        var provider = await Providers().GetAsync(id, CancellationToken.None);
        provider.Should().BeEquivalentTo(new
        {
            Name = "Keycloak", ProviderType = "oidc", IssuerUrl = "https://issuer.example", ClientId = "nocturne",
            ClientSecretEncrypted = new byte[] { 1, 2, 3 }, ClaimMappingsJson = "{\"a\":\"b\"}", IsEnabled = true,
        });
        provider!.Scopes.Should().Equal("openid", "email");
        provider.DefaultRoles.Should().Equal("readable");
        (await Providers().FindByIssuerAsync("https://issuer.example/", CancellationToken.None))!.Id.Should().Be(id);
        (await Providers().FindByIssuerAsync("https://issuer.example", CancellationToken.None))!.Id.Should().Be(id);
        (await Providers().FindByIssuerAsync("https://other.example", CancellationToken.None)).Should().BeNull();
        (await Providers().LastModifiedAsync(CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task TheListIsOrderedAndCanBeNarrowedToTheEnabled()
    {
        await Providers().CreateAsync(new NewOidcProvider("Zed", "oidc", "https://z.example", "c", DisplayOrder: 0), CancellationToken.None);
        await Providers().CreateAsync(new NewOidcProvider("Alpha", "oidc", "https://a.example", "c", DisplayOrder: 0), CancellationToken.None);
        await Providers().CreateAsync(new NewOidcProvider("Off", "oidc", "https://o.example", "c", DisplayOrder: -1, IsEnabled: false), CancellationToken.None);

        (await Providers().ListAsync(enabledOnly: false, CancellationToken.None)).Select(p => p.Name).Should().Equal("Off", "Alpha", "Zed");
        (await Providers().ListAsync(enabledOnly: true, CancellationToken.None)).Select(p => p.Name).Should().Equal("Alpha", "Zed");
    }

    [Fact]
    public async Task AnEditKeepsTheSecretUnlessItBringsOne()
    {
        var id = await Providers().CreateAsync(Keycloak(), CancellationToken.None);
        await AgeAsync(id);

        var kept = await Providers().UpdateAsync(id, new OidcProviderEdit(
            "KC", "oauth2", "{\"token\":\"t\"}", "https://issuer.example", "client2", null, ["read"], "{}", ["admin"], false, 3), CancellationToken.None);
        kept!.UpdatedAt.Should().BeAfter(Past);
        kept.Should().BeEquivalentTo(new
        {
            Name = "KC", ProviderType = "oauth2", OAuth2SettingsJson = "{\"token\":\"t\"}", ClientId = "client2",
            ClientSecretEncrypted = new byte[] { 1, 2, 3 }, IsEnabled = false, DisplayOrder = 3,
        });

        var replaced = await Providers().UpdateAsync(id, new OidcProviderEdit(
            "KC", "oauth2", null, "https://issuer.example", "client2", [9], ["read"], "{}", ["admin"], true, 3), CancellationToken.None);
        replaced!.ClientSecretEncrypted.Should().Equal(9);
        (await Providers().UpdateAsync(Guid.CreateVersion7(), new OidcProviderEdit("x", "oidc", null, "i", "c", null, [], "{}", [], true, 0), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task EnablingDiscoveryCachingAndDeletingAnswerForTheRow()
    {
        var id = await Providers().CreateAsync(Keycloak(enabled: false), CancellationToken.None);
        await AgeAsync(id);

        (await Providers().SetEnabledAsync(id, true, CancellationToken.None)).Should().BeTrue();
        (await Providers().GetAsync(id, CancellationToken.None))!.UpdatedAt.Should().BeAfter(Past);
        (await Providers().CacheDiscoveryDocumentAsync(id, "{\"issuer\":\"x\"}", Now, CancellationToken.None)).Should().BeTrue();
        (await Providers().GetAsync(id, CancellationToken.None)).Should().BeEquivalentTo(new
        {
            IsEnabled = true, DiscoveryDocumentJson = "{\"issuer\":\"x\"}", DiscoveryCachedAt = (DateTime?)Now,
        });

        (await Providers().DeleteAsync(id, CancellationToken.None)).Should().BeTrue();
        (await Providers().GetAsync(id, CancellationToken.None)).Should().BeNull();
        (await Providers().DeleteAsync(id, CancellationToken.None)).Should().BeFalse();
        (await Providers().SetEnabledAsync(id, true, CancellationToken.None)).Should().BeFalse();
        (await Providers().CacheDiscoveryDocumentAsync(id, "{}", Now, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ASyncOfAnUnchangedProviderRecordsTheTimeTheCallerGave()
    {
        var id = Guid.CreateVersion7();
        NewOidcProvider Configured() => new("Keycloak", "oidc", "https://issuer.example", "nocturne", Id: id, Scopes: ["openid"], DefaultRoles: ["readable"]);
        await Providers().CreateAsync(Configured(), CancellationToken.None);
        await AgeAsync(id);

        await Providers().SyncConfiguredAsync([Configured()], Now, CancellationToken.None);

        (await Providers().GetAsync(id, CancellationToken.None))!.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task TheConfigSyncUpdatesAddsAndDisablesInOneSave()
    {
        var keep = Guid.CreateVersion7();
        var stale = Guid.CreateVersion7();
        var added = Guid.CreateVersion7();
        await Providers().CreateAsync(Keycloak(keep), CancellationToken.None);
        await Providers().CreateAsync(new NewOidcProvider("Stale", "oidc", "https://stale.example", "c", Id: stale), CancellationToken.None);
        await AgeAsync(keep, stale);

        await Providers().SyncConfiguredAsync(
        [
            new NewOidcProvider("Keycloak 2", "oidc", "https://issuer.example", "renamed", Id: keep, DisplayOrder: 7, Scopes: ["openid"], DefaultRoles: ["admin"]),
            new NewOidcProvider("GitHub", "oauth2", "https://github.com", "gh", Id: added, OAuth2SettingsJson: "{\"token\":\"t\"}", Icon: "github"),
        ], Now, CancellationToken.None);

        var kept = await Providers().GetAsync(keep, CancellationToken.None);
        kept.Should().BeEquivalentTo(new
        {
            Name = "Keycloak 2", ClientId = "renamed", DisplayOrder = 7,
            ClientSecretEncrypted = new byte[] { 1, 2, 3 }, ClaimMappingsJson = "{\"a\":\"b\"}",
        }, "the sync carries no secret or claim mapping, so the stored ones stay");
        kept!.Scopes.Should().Equal("openid");
        (await Providers().GetAsync(stale, CancellationToken.None)).Should().BeEquivalentTo(new { IsEnabled = false });
        (await Providers().GetAsync(stale, CancellationToken.None))!.UpdatedAt.Should().BeAfter(Past);
        kept.UpdatedAt.Should().NotBe(Now, "the save stamps a row whose columns changed, over the caller's time");
        (await Providers().GetAsync(added, CancellationToken.None)).Should().BeEquivalentTo(new
        {
            Name = "GitHub", ProviderType = "oauth2", OAuth2SettingsJson = "{\"token\":\"t\"}", Icon = "github", IsEnabled = true,
        });

        var noId = () => Providers().SyncConfiguredAsync([new NewOidcProvider("x", "oidc", "i", "c")], Now, CancellationToken.None);
        await noId.Should().ThrowAsync<ArgumentException>();
    }
}
