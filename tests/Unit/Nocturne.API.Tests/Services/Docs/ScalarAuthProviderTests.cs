using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Docs;
using Nocturne.Core.Contracts.Auth;
using Xunit;

namespace Nocturne.API.Tests.Services.Docs;

/// <summary>
/// The documentation paths are served on every host without tenant resolution or
/// authentication, so what they expose is decided entirely here: whether the host's tenant
/// opted in at all, an OAuth client for that tenant, and a bearer token only when it is a demo.
/// </summary>
public class ScalarAuthProviderTests : IDisposable
{
    private const string BaseDomain = DocsTenantFixture.BaseDomain;

    private readonly DocsTenantFixture _fixture = new();

    [Fact]
    public async Task TryPrepareAsync_RegistersClientAndPrefillsToken_OnADemoHost()
    {
        var tenantId = _fixture.SeedTenant("demo", isDemo: true, withDemoMember: true);
        var context = DocsTenantFixture.BuildContext("demo.nocturne.run");

        (await _fixture.BuildProvider().TryPrepareAsync(context)).Should().BeTrue();

        var auth = Auth(context);
        auth.Should().NotBeNull();
        auth!.ClientId.Should().NotBeNullOrWhiteSpace();
        auth.RedirectUri.Should().Be("https://demo.nocturne.run/scalar");
        auth.BearerToken.Should().Be("demo-access-token");

        await using var db = _fixture.Db();
        var client = await db.OAuthClients.IgnoreQueryFilters()
            .SingleAsync(c => c.TenantId == tenantId && c.SoftwareId == ScalarAuthProvider.ScalarSoftwareId);
        JsonSerializer.Deserialize<List<string>>(client.RedirectUris)
            .Should().Equal("https://demo.nocturne.run/scalar");
    }

    [Fact]
    public async Task TryPrepareAsync_RegistersClientWithoutAToken_OnARealTenantHost()
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = DocsTenantFixture.BuildContext("rhys.nocturne.run");

        (await _fixture.BuildProvider().TryPrepareAsync(context)).Should().BeTrue();

        var auth = Auth(context);
        auth.Should().NotBeNull();
        auth!.RedirectUri.Should().Be("https://rhys.nocturne.run/scalar");
        auth.BearerToken.Should().BeNull("only a demo tenant's account may be handed out");
        _fixture.SessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A tenant that never asked for the reference must not have one — and must not have an
    /// OAuth client written on it by the request that asked.
    /// </summary>
    [Theory]
    [InlineData("/scalar")]
    [InlineData("/openapi/nocturne.json")]
    public async Task TryPrepareAsync_RefusesATenantThatHasNotOptedIn(string path)
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false, allowPublicDocs: false);
        var context = DocsTenantFixture.BuildContext("rhys.nocturne.run", path: path);

        (await _fixture.BuildProvider().TryPrepareAsync(context)).Should().BeFalse();

        Auth(context).Should().BeNull();
        await using var db = _fixture.Db();
        (await db.OAuthClients.IgnoreQueryFilters().AnyAsync())
            .Should().BeFalse("nothing may be registered on a tenant with no documentation surface");
    }

    /// <summary>
    /// The scheme and the port both come from headers the caller controls, so neither may decide
    /// whether the docs are served — only whether an OAuth client is registered.
    /// </summary>
    [Theory]
    [InlineData("https", "rhys.nocturne.run:8443")]
    [InlineData("javascript", "rhys.nocturne.run")]
    [InlineData("http", "rhys.nocturne.run")]
    public async Task TryPrepareAsync_RefusesAnOptedOutTenant_WhateverTheSchemeOrPort(
        string proto, string host)
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false, allowPublicDocs: false);
        var context = DocsTenantFixture.BuildContext(host, proto);

        (await _fixture.BuildProvider().TryPrepareAsync(context)).Should().BeFalse();
    }

    /// <summary>
    /// Positive control for the theory above: the same odd origins on a tenant that <em>has</em>
    /// opted in are still served, so the refusals there come from the opt-in and not from the
    /// origin being rejected outright.
    /// </summary>
    [Theory]
    [InlineData("https", "rhys.nocturne.run:8443")]
    [InlineData("javascript", "rhys.nocturne.run")]
    [InlineData("http", "rhys.nocturne.run")]
    public async Task TryPrepareAsync_ServesAnOptedInTenant_ButRegistersNoClient_OnAnOddOrigin(
        string proto, string host)
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = DocsTenantFixture.BuildContext(host, proto);

        (await _fixture.BuildProvider().TryPrepareAsync(context)).Should().BeTrue();

        Auth(context).Should().BeNull("a caller must not register origins of its own choosing");
        await using var db = _fixture.Db();
        (await db.OAuthClients.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Positive control for the gate: it can only be trusted to refuse if it is shown letting
    /// something through.
    /// </summary>
    [Theory]
    [InlineData(BaseDomain)]              // apex of an instance with no tenants
    [InlineData("nope.nocturne.run")]     // a slug nobody has
    public async Task TryPrepareAsync_ServesTheDocs_WhenNoTenantResolves(string host)
    {
        var context = DocsTenantFixture.BuildContext(host);

        (await _fixture.BuildProvider().TryPrepareAsync(context)).Should().BeTrue();

        Auth(context).Should().BeNull();
    }

    [Fact]
    public async Task TryPrepareAsync_ServesTheSpecs_WithoutRegisteringAClient()
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = DocsTenantFixture.BuildContext("rhys.nocturne.run", path: "/openapi/nocturne.json");

        (await _fixture.BuildProvider().TryPrepareAsync(context)).Should().BeTrue();

        Auth(context).Should().BeNull("the specs are static — only the reference UI signs in");
        await using var db = _fixture.Db();
        (await db.OAuthClients.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// The resolution is cached, so a toggle that did not evict it would leave the previous
    /// answer standing for the cache lifetime.
    /// </summary>
    [Fact]
    public async Task EvictTenant_LetsAToggleTakeEffectBeforeTheCacheExpires()
    {
        var tenantId = _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var cache = new MemoryCache(new MemoryCacheOptions());

        (await _fixture.BuildProvider(cache).TryPrepareAsync(DocsTenantFixture.BuildContext("rhys.nocturne.run")))
            .Should().BeTrue();

        _fixture.SetAllowPublicDocs(tenantId, false);

        (await _fixture.BuildProvider(cache).TryPrepareAsync(DocsTenantFixture.BuildContext("rhys.nocturne.run")))
            .Should().BeTrue("the cached resolution still says the tenant opted in");

        ScalarAuthProvider.EvictTenant(cache, "rhys");

        (await _fixture.BuildProvider(cache).TryPrepareAsync(DocsTenantFixture.BuildContext("rhys.nocturne.run")))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TryPrepareAsync_DoesNothing_OnAShareHost()
    {
        _fixture.SeedTenant("demo", isDemo: true, withDemoMember: true);
        var context = DocsTenantFixture.BuildContext("sometoken.share.nocturne.run");

        await _fixture.BuildProvider().TryPrepareAsync(context);

        Auth(context).Should().BeNull("a share link grants read-only anonymous access only");
        await using var db = _fixture.Db();
        (await db.OAuthClients.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task TryPrepareAsync_DoesNothing_ForAnUnknownSlug()
    {
        _fixture.SeedTenant("demo", isDemo: true, withDemoMember: true);
        var context = DocsTenantFixture.BuildContext("nope.nocturne.run");

        await _fixture.BuildProvider().TryPrepareAsync(context);

        Auth(context).Should().BeNull();
    }

    [Fact]
    public async Task TryPrepareAsync_DoesNothing_ForAnInactiveTenant()
    {
        _fixture.SeedTenant("demo", isDemo: true, withDemoMember: true, isActive: false);
        var context = DocsTenantFixture.BuildContext("demo.nocturne.run");

        await _fixture.BuildProvider().TryPrepareAsync(context);

        Auth(context).Should().BeNull();
    }

    [Fact]
    public async Task TryPrepareAsync_MarksTheDemoResponseUncacheable()
    {
        _fixture.SeedTenant("demo", isDemo: true, withDemoMember: true);
        var context = DocsTenantFixture.BuildContext("demo.nocturne.run");

        await _fixture.BuildProvider().TryPrepareAsync(context);

        context.Response.Headers.CacheControl.ToString()
            .Should().Contain("no-store", "the page carries a bearer token");
    }

    [Fact]
    public async Task TryPrepareAsync_LeavesARealTenantResponseCacheable()
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = DocsTenantFixture.BuildContext("rhys.nocturne.run");

        await _fixture.BuildProvider().TryPrepareAsync(context);

        context.Response.Headers.CacheControl.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task TryPrepareAsync_DoesNotDuplicateARedirectUri()
    {
        var tenantId = _fixture.SeedTenant("demo", isDemo: true, withDemoMember: true);

        // A fresh provider per call so the in-memory client cache does not mask a
        // repeated registration.
        await _fixture.BuildProvider().TryPrepareAsync(DocsTenantFixture.BuildContext("demo.nocturne.run"));
        await _fixture.BuildProvider().TryPrepareAsync(DocsTenantFixture.BuildContext("demo.nocturne.run"));

        await using var db = _fixture.Db();
        var client = await db.OAuthClients.IgnoreQueryFilters()
            .SingleAsync(c => c.TenantId == tenantId);
        JsonSerializer.Deserialize<List<string>>(client.RedirectUris)
            .Should().Equal("https://demo.nocturne.run/scalar");
    }

    [Fact]
    public async Task TryPrepareAsync_StopsAddingRedirectUrisAtTheCap()
    {
        // Unreachable by construction — the URI comes from configuration plus the stored slug,
        // and only https survives on a public host — but the row is written by an unauthenticated
        // request, so the bound must not rest on that argument staying true. Exercised directly
        // because nothing else can reach it.
        var tenantId = _fixture.SeedTenant("demo", isDemo: true, withDemoMember: true);

        await _fixture.BuildProvider().TryPrepareAsync(DocsTenantFixture.BuildContext("demo.nocturne.run"));

        await using (var seed = _fixture.Db())
        {
            var client = await seed.OAuthClients.IgnoreQueryFilters()
                .SingleAsync(c => c.TenantId == tenantId);
            client.RedirectUris = JsonSerializer.Serialize(
                Enumerable.Range(0, ScalarAuthProvider.MaxRedirectUris)
                    .Select(i => $"https://origin{i}.example/scalar")
                    .ToList());
            await seed.SaveChangesAsync();
        }

        var context = DocsTenantFixture.BuildContext("demo.nocturne.run");
        await _fixture.BuildProvider().TryPrepareAsync(context);

        context.Items.Should().NotContainKey(ScalarAuthContext.HttpContextItemKey,
            "at the cap the provider declines rather than growing the row");

        await using var db = _fixture.Db();
        var after = await db.OAuthClients.IgnoreQueryFilters()
            .SingleAsync(c => c.TenantId == tenantId);
        JsonSerializer.Deserialize<List<string>>(after.RedirectUris)
            .Should().HaveCount(ScalarAuthProvider.MaxRedirectUris);
    }

    /// <summary>
    /// The request host is client-controllable — the gateway forwards X-Forwarded-Host
    /// untouched and UseForwardedHeaders trusts any proxy. It may therefore only select a
    /// tenant, never reach the redirect URI: byte-exact authorize matching means a
    /// persisted attacker origin would have that tenant's authorization codes delivered to
    /// a host they control.
    /// </summary>
    [Theory]
    [InlineData("attacker.example")]                        // belongs to nobody
    [InlineData("evil.attacker.example")]
    [InlineData("nocturne.run.attacker.example")]           // base domain as a prefix
    [InlineData("rhys.nocturne.run.attacker.example")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("0x7f.1")]                                  // parses as loopback
    [InlineData("[::1]")]
    public async Task TryPrepareAsync_RejectsAHostThisDeploymentDoesNotServe(string host)
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = DocsTenantFixture.BuildContext(host);

        await _fixture.BuildProvider().TryPrepareAsync(context);

        Auth(context).Should().BeNull();

        await using var db = _fixture.Db();
        (await db.OAuthClients.IgnoreQueryFilters().AnyAsync())
            .Should().BeFalse("a foreign host must not register a redirect URI on any tenant");
    }

    [Fact]
    public async Task TryPrepareAsync_RejectsAForeignHost_EvenOnASingleTenantInstall()
    {
        // The apex branch serves single-tenant installs. It must not treat a host that is
        // not the configured apex as the apex, or the sole tenant absorbs any origin.
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = DocsTenantFixture.BuildContext("attacker.example");

        await _fixture.BuildProvider().TryPrepareAsync(context);

        Auth(context).Should().BeNull();
    }

    [Fact]
    public async Task TryPrepareAsync_ResolvesTheSoleTenant_OnTheConfiguredApex()
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = DocsTenantFixture.BuildContext(BaseDomain);

        await _fixture.BuildProvider().TryPrepareAsync(context);

        Auth(context)!.RedirectUri.Should().Be($"https://{BaseDomain}/scalar");
    }

    /// <summary>
    /// The apex of a single-tenant install resolves to that tenant, so its opt-in governs the
    /// apex too — otherwise turning the docs off would leave them served from the front door.
    /// </summary>
    [Fact]
    public async Task TryPrepareAsync_RefusesTheApex_WhenTheSoleTenantHasNotOptedIn()
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false, allowPublicDocs: false);
        var context = DocsTenantFixture.BuildContext(BaseDomain);

        (await _fixture.BuildProvider().TryPrepareAsync(context)).Should().BeFalse();
    }

    [Theory]
    [InlineData("rhys.nocturne.run:8443")]
    [InlineData("rhys.nocturne.run:80")]
    public async Task TryPrepareAsync_RejectsAPortTheDeploymentIsNotServedOn(string host)
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = DocsTenantFixture.BuildContext(host);

        await _fixture.BuildProvider().TryPrepareAsync(context);

        Auth(context).Should().BeNull("a caller must not register origins differing by port");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("file")]
    [InlineData("HTTPS evil")]
    public async Task TryPrepareAsync_RejectsANonHttpForwardedProto(string proto)
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = DocsTenantFixture.BuildContext("rhys.nocturne.run", proto);

        await _fixture.BuildProvider().TryPrepareAsync(context);

        Auth(context).Should().BeNull();
    }

    [Fact]
    public async Task TryPrepareAsync_RejectsCleartextHttpOnAPublicHost()
    {
        // RedirectUriValidator treats http on a non-loopback host as invalid for
        // registration; authorization codes must not be issued over plaintext.
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false);
        var context = DocsTenantFixture.BuildContext("rhys.nocturne.run", proto: "http");

        await _fixture.BuildProvider().TryPrepareAsync(context);

        Auth(context).Should().BeNull();
        await using var db = _fixture.Db();
        (await db.OAuthClients.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task TryPrepareAsync_DoesNotBadgeTheClientAsKnown()
    {
        // The consent screen suppresses its "app not recognized" warning for known
        // clients. This row is created by an unauthenticated request.
        var tenantId = _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false);

        await _fixture.BuildProvider().TryPrepareAsync(DocsTenantFixture.BuildContext("rhys.nocturne.run"));

        await using var db = _fixture.Db();
        var client = await db.OAuthClients.IgnoreQueryFilters()
            .SingleAsync(c => c.TenantId == tenantId);
        client.IsKnown.Should().BeFalse();
        client.ClientId.Should().NotBe(ScalarAuthProvider.ScalarSoftwareId);
    }

    private static ScalarAuthContext? Auth(HttpContext context) =>
        context.Items[ScalarAuthContext.HttpContextItemKey] as ScalarAuthContext;

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }
}
