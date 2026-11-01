using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="OAuthClientRepository"/> over SQLite: a registered client read back by either id, the
/// pinned find-or-create of a software client and its redirect URIs, and the pinned sweep of the
/// clients nothing names.
/// </summary>
public class OAuthClientRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);
    private const string Docs = "dev.example.docs";

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly Guid _subject = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public OAuthClientRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant).SeedTenant(_otherTenant, "other");
        using var db = _db.CreateContext();
        db.Subjects.Add(new SubjectEntity { Id = _subject, Name = "Owner" });
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private OAuthClientRepository Clients(Guid? tenant = null) =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(tenant ?? _tenant)));

    private static NewOAuthClient Registration(string clientId, string? softwareId = null, params string[] redirectUris) =>
        new(clientId, softwareId, "App", "https://app.example", "https://app.example/logo.png", "App Display",
            IsKnown: false, redirectUris, CreatedFromIp: "203.0.113.7");

    private async Task<OAuthClientRecord> CreatedAtAsync(Guid tenant, string clientId, DateTime createdAt, string? softwareId = null)
    {
        var client = await Clients(tenant).CreateAsync(Registration(clientId, softwareId), CancellationToken.None);
        await using var db = _db.CreateContext(tenant);
        await db.OAuthClients.Where(c => c.Id == client.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.CreatedAt, createdAt));
        return client;
    }

    private async Task<List<string>> RemainingAsync()
    {
        await using var db = _db.CreateContext(Guid.Empty);
        return await db.OAuthClients.IgnoreQueryFilters().OrderBy(c => c.ClientId).Select(c => c.ClientId).ToListAsync();
    }

    [Fact]
    public async Task ARegisteredClientIsReadBackByEitherIdOnItsTenantOnly()
    {
        var created = await Clients().CreateAsync(
            Registration("app", "org.example.app", "https://app.example/cb", "http://127.0.0.1/cb"), CancellationToken.None);

        var byClientId = await Clients().FindByClientIdAsync("app", CancellationToken.None);
        var byId = await Clients().GetAsync(created.Id, CancellationToken.None);

        byClientId.Should().BeEquivalentTo(byId);
        byClientId!.TenantId.Should().Be(_tenant);
        byClientId.SoftwareId.Should().Be("org.example.app");
        byClientId.ClientName.Should().Be("App");
        byClientId.DisplayName.Should().Be("App Display");
        byClientId.ClientUri.Should().Be("https://app.example");
        byClientId.LogoUri.Should().Be("https://app.example/logo.png");
        byClientId.CreatedFromIp.Should().Be("203.0.113.7");
        byClientId.RedirectUris.Should().Equal("https://app.example/cb", "http://127.0.0.1/cb");
        (await Clients(_otherTenant).FindByClientIdAsync("app", CancellationToken.None)).Should().BeNull();
        (await Clients(_otherTenant).GetAsync(created.Id, CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    public async Task AnUnreadableRedirectUriListReadsAsEmpty(string stored)
    {
        await using (var db = _db.CreateContext())
        {
            db.OAuthClients.Add(new OAuthClientEntity { Id = Guid.CreateVersion7(), ClientId = "odd", RedirectUris = stored });
            await db.SaveChangesAsync();
        }

        (await Clients().FindByClientIdAsync("odd", CancellationToken.None))!.RedirectUris.Should().BeEmpty();
    }

    [Fact]
    public async Task ACreateOnAUnitLandsWhenTheOwnerSaves()
    {
        await using (var unit = DbUnitOfWork.Own(_db.CreateContext()))
        {
            await new OAuthClientRepository(unit).CreateAsync(Registration("staged"), CancellationToken.None);
            (await Clients().FindByClientIdAsync("staged", CancellationToken.None)).Should().BeNull();
            await unit.Context.SaveChangesAsync();
        }

        (await Clients().FindByClientIdAsync("staged", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task ASoftwareClientIsCreatedOncePerTenantAndGainsRedirectUrisUpToTheCap()
    {
        var asOther = Clients(_otherTenant);

        var first = await asOther.FindOrCreateWithRedirectUriAsync(
            _tenant, Registration("docs-1", Docs, "ignored"), "https://a.example/docs/", 2, CancellationToken.None);
        var again = await asOther.FindOrCreateWithRedirectUriAsync(
            _tenant, Registration("docs-2", Docs), "https://a.example/docs/", 2, CancellationToken.None);
        var second = await asOther.FindOrCreateWithRedirectUriAsync(
            _tenant, Registration("docs-3", Docs), "https://b.example/docs/", 2, CancellationToken.None);
        var capped = await asOther.FindOrCreateWithRedirectUriAsync(
            _tenant, Registration("docs-4", Docs), "https://c.example/docs/", 2, CancellationToken.None);
        var onOther = await Clients().FindOrCreateWithRedirectUriAsync(
            _otherTenant, Registration("docs-5", Docs), "https://a.example/docs/", 2, CancellationToken.None);

        first.Should().Be(new OAuthClientRedirectUriRegistration("docs-1", true, 1));
        again.Should().Be(new OAuthClientRedirectUriRegistration("docs-1", true, 1));
        second.Should().Be(new OAuthClientRedirectUriRegistration("docs-1", true, 2));
        capped.Should().Be(new OAuthClientRedirectUriRegistration("docs-1", false, 2));
        onOther.Should().Be(new OAuthClientRedirectUriRegistration("docs-5", true, 1));

        var stored = await Clients().FindByClientIdAsync("docs-1", CancellationToken.None);
        stored!.TenantId.Should().Be(_tenant);
        stored.SoftwareId.Should().Be(Docs);
        stored.RedirectUris.Should().Equal("https://a.example/docs/", "https://b.example/docs/");
        (await RemainingAsync()).Should().Equal("docs-1", "docs-5");
    }

    [Fact]
    public async Task AFindOrCreateNeedsASoftwareId()
    {
        var act = () => Clients().FindOrCreateWithRedirectUriAsync(
            _tenant, Registration("none"), "https://a.example/", 5, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TheSweepDeletesOnlyTheOldClientsNothingNamesOnThePinnedTenant()
    {
        var cutoff = Now.AddHours(-24);
        var old = cutoff.AddHours(-1);
        await CreatedAtAsync(_tenant, "unused", old);
        await CreatedAtAsync(_tenant, "young", cutoff.AddHours(1));
        await CreatedAtAsync(_tenant, "docs", old, Docs);
        await CreatedAtAsync(_tenant, "other-software", old, "org.example.other");
        var granted = await CreatedAtAsync(_tenant, "granted", old);
        var coded = await CreatedAtAsync(_tenant, "coded", old);
        await CreatedAtAsync(_tenant, "device", old);
        await CreatedAtAsync(_otherTenant, "elsewhere", old);

        await using (var db = _db.CreateContext())
        {
            db.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                ClientEntityId = granted.Id,
                SubjectId = _subject,
                Scopes = ["glucose.read"],
                GrantType = "app",
                RevokedAt = old,
            });
            await db.SaveChangesAsync();
        }
        var tokens = new OAuthTokenRepository(new TestNocturneDbContextFactory(_db.CreateContext(_tenant)));
        await tokens.StoreAuthorizationCodeAsync(
            new NewAuthorizationCode(coded.Id, _subject, "code", ["glucose.read"], "https://app.example/cb", "challenge",
                Now.AddMinutes(10), LimitTo24Hours: false), CancellationToken.None);
        await tokens.StoreDeviceCodeAsync(
            new NewDeviceCode("device", "device-hash", "BCDFGHJK", ["glucose.read"], Now.AddMinutes(10), 5), CancellationToken.None);

        var deleted = await Clients(_otherTenant).DeleteUnreferencedAsync(_tenant, cutoff, Docs, CancellationToken.None);

        deleted.Should().Be(2);
        (await RemainingAsync()).Should().Equal("coded", "device", "docs", "elsewhere", "granted", "young");
    }
}
