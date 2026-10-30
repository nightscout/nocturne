using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="OAuthGrantRepository"/> over SQLite: the pinned reads the authentication handlers
/// take, the writes a grant's life is made of, and the revoke that lands a grant with its tokens
/// on one unit of work.
/// </summary>
public class OAuthGrantRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);
    private const string Digest = "0123456789abcdef0123456789abcdef01234567";

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly Guid _member = Guid.CreateVersion7();
    private readonly Guid _stranger = Guid.CreateVersion7();
    private readonly Guid _client = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public OAuthGrantRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant).SeedTenant(_otherTenant, "other");
        using var db = _db.CreateContext();
        db.Subjects.Add(new SubjectEntity { Id = _member, Name = "Member" });
        db.Subjects.Add(new SubjectEntity { Id = _stranger, Name = "Stranger" });
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenant,
            SubjectId = _member,
            DirectPermissions = [Scope.GlucoseRead],
            LimitTo24Hours = true,
        });
        db.OAuthClients.Add(new OAuthClientEntity { Id = _client, ClientId = "app", DisplayName = "App", RedirectUris = "[]" });
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private OAuthGrantRepository Grants(Guid? tenant = null) =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(tenant ?? _tenant)));

    private OAuthTokenRepository Tokens() => new(new TestNocturneDbContextFactory(_db.CreateContext(_tenant)));

    private static NewOAuthGrant Direct(Guid subject, string hash, DateTime? expiresAt = null, string? legacySecretHash = null) =>
        new(null, subject, OAuthGrantTypes.Direct, [Scope.GlucoseRead, Scope.TreatmentsRead], "uploader",
            hash, legacySecretHash, expiresAt, LimitTo24Hours: false);

    private Task<Guid> CreateAsync(NewOAuthGrant grant) => Grants().CreateAsync(grant, CancellationToken.None);

    private async Task<OAuthGrantRecord> GetAsync(Guid id) =>
        (await Grants().GetAsync(id, CancellationToken.None))!;

    private async Task<Guid> AdoptedAsync(Guid subject) =>
        await Grants().AdoptLegacyCredentialAsync(
            new AdoptedLegacyCredential(subject, "Reader", [Scope.GlucoseRead], LegacyTokenDigest: Digest), CancellationToken.None);

    [Fact]
    public async Task ALiveDirectGrantIsFoundByEachCredentialWithItsMembershipFromAnyScope()
    {
        var grant = await CreateAsync(Direct(_member, "h1", legacySecretHash: "s1"));
        var adopted = await AdoptedAsync(_stranger);
        var fromAnotherScope = Grants(_otherTenant);

        var byHash = await fromAnotherScope.FindLiveDirectGrantAsync(
            _tenant, new DirectGrantLookup.ByTokenHash(["other", "h1"]), Now, CancellationToken.None);
        var bySecret = await fromAnotherScope.FindLiveDirectGrantAsync(
            _tenant, new DirectGrantLookup.ByLegacySecretHash("s1"), Now, CancellationToken.None);
        var byDigest = await fromAnotherScope.FindLiveDirectGrantAsync(
            _tenant, new DirectGrantLookup.ByLegacyTokenDigestPrefix(Digest[..16]), Now, CancellationToken.None);

        byHash!.Grant.Should().Be(new DirectGrantCredential(
            grant, _member, byHash.Grant.Scopes, "uploader", false, null, IsMigrated: false, "h1", "s1"));
        byHash.Grant.Scopes.Should().Equal(Scope.GlucoseRead, Scope.TreatmentsRead);
        byHash.Membership!.Access.EffectivePermissions.Should().Equal(Scope.GlucoseRead);
        byHash.Membership.Access.LimitTo24Hours.Should().BeTrue();
        bySecret!.Grant.Id.Should().Be(grant);
        byDigest!.Grant.Id.Should().Be(adopted);
        byDigest.Grant.IsMigrated.Should().BeTrue();
        byDigest.Membership.Should().BeNull("the holder is no member of the tenant");
        (await fromAnotherScope.FindLiveDirectGrantAsync(
            _tenant, new DirectGrantLookup.ByLegacyTokenDigestPrefix(Digest[8..24]), Now, CancellationToken.None))
            .Should().BeNull("a slice from the middle of the digest is not a prefix of it");
    }

    [Fact]
    public async Task ThePinDecidesTheTenantAndTheExpiryInstantDoesNotAuthenticate()
    {
        var grant = await GetAsync(await CreateAsync(Direct(_member, "h1", expiresAt: Now)));

        (await Grants().FindLiveDirectGrantAsync(
            _tenant, new DirectGrantLookup.ByTokenHash(["h1"]), Now.AddSeconds(-1), CancellationToken.None)).Should().NotBeNull();
        (await Grants().FindLiveDirectGrantAsync(
            _tenant, new DirectGrantLookup.ByTokenHash(["h1"]), Now, CancellationToken.None)).Should().BeNull();
        (await Grants().FindLiveDirectGrantAsync(
            _otherTenant, new DirectGrantLookup.ByTokenHash(["h1"]), Now.AddSeconds(-1), CancellationToken.None))
            .Should().BeNull("the grant is matched on the pinned tenant");
        grant.IsLiveDirectGrantAt(Now.AddSeconds(-1)).Should().BeTrue();
        grant.IsLiveDirectGrantAt(Now).Should().BeFalse("the record judges liveness by the store's rule");
    }

    [Fact]
    public async Task TheStateReadIsPinnedAndTheUseStampKeepsAnAddressItIsNotGiven()
    {
        var grant = await CreateAsync(Direct(_member, "h1", expiresAt: Now.AddDays(1)));

        var state = await Grants(_otherTenant).GetStateAsync(_tenant, grant, CancellationToken.None);
        state.Should().Be(new OAuthGrantState(null, Now.AddDays(1), state!.Scopes, false));
        (await Grants().GetStateAsync(_otherTenant, grant, CancellationToken.None)).Should().BeNull();

        await Grants(_otherTenant).RecordUseAsync(
            _tenant, grant, Now, new GrantPresentation("203.0.113.7", "xDrip"), CancellationToken.None);
        await Grants().RecordUseAsync(grant, Now.AddMinutes(1), presentation: null, CancellationToken.None);

        var stored = await GetAsync(grant);
        stored.LastUsedAt.Should().Be(Now.AddMinutes(1));
        stored.LastUsedIp.Should().Be("203.0.113.7");
        stored.LastUsedUserAgent.Should().Be("xDrip");
        stored.TenantId.Should().Be(_tenant, "the tenant is stamped on save and read back");
    }

    [Fact]
    public async Task AGuestCodeIsActivatedOnceAndAConsentEditKeepsItsDeviceCodeMark()
    {
        var guest = await CreateAsync(new NewOAuthGrant(
            null, _member, OAuthGrantTypes.Guest, [Scope.GlucoseRead], "Mum", "code", null, Now.AddHours(48),
            LimitTo24Hours: true, CreatedBySubjectId: _stranger));
        var app = await GetAsync(await CreateAsync(new NewOAuthGrant(
            _client, _member, OAuthGrantTypes.App, [Scope.GlucoseRead], null, null, null, null,
            LimitTo24Hours: false, ApprovedByDeviceCode: true)));

        var activated = await Grants().ActivateGuestAsync("code", Now, new GrantPresentation("ip", "ua"), CancellationToken.None);
        activated!.Id.Should().Be(guest);
        activated.ActivatedAt.Should().Be(Now);
        activated.CreatedBySubjectId.Should().Be(_stranger);
        (await Grants().ActivateGuestAsync("code", Now, new GrantPresentation("ip", "ua"), CancellationToken.None)).Should().BeNull();
        (await GetAsync(guest)).ActivatedIp.Should().Be("ip");
        app.Client!.ClientId.Should().Be("app", "a read by id carries the client");

        var edited = await Grants().UpdateAsync(
            app.Id, new OAuthGrantEdit(Label: "Phone", Scopes: [Scope.TreatmentsRead], LimitTo24Hours: true), CancellationToken.None);

        edited!.Label.Should().Be("Phone");
        edited.Scopes.Should().Equal(Scope.TreatmentsRead);
        edited.LimitTo24Hours.Should().BeTrue();
        edited.Client!.DisplayName.Should().Be("App");
        edited.ClientIdentity.Should().Be(app.ClientIdentity, "a device-code approval is never cleared");
        (await Grants().FindOfSubjectAsync(app.Id, _stranger, CancellationToken.None)).Should().BeNull();
        (await Grants().GetClientDisplayNamesAsync([app.Id, guest], CancellationToken.None))
            .Should().BeEquivalentTo(new Dictionary<Guid, string?> { [app.Id] = "App", [guest] = null });
    }

    [Fact]
    public async Task ARevokeOnAUnitLandsTheGrantsWithTheirTokensInOneSave()
    {
        var target = await CreateAsync(Direct(_member, "h1"));
        var spared = await CreateAsync(Direct(_member, "h2"));
        var tokens = Tokens();
        var live = await tokens.StoreRefreshTokenAsync(new NewOAuthRefreshToken(_tenant, target, "t1", Now.AddDays(90)), CancellationToken.None);
        var rotated = await tokens.StoreRefreshTokenAsync(new NewOAuthRefreshToken(_tenant, target, "t0", Now.AddDays(90)), CancellationToken.None);
        var sparedToken = await tokens.StoreRefreshTokenAsync(new NewOAuthRefreshToken(_tenant, spared, "t2", Now.AddDays(90)), CancellationToken.None);
        (await tokens.TryRotateRefreshTokenAsync(rotated, live, Now.AddMinutes(-1), CancellationToken.None)).Should().BeTrue();

        await using (var unit = DbUnitOfWork.Own(_db.CreateContext()))
        {
            (await new OAuthGrantRepository(unit).RevokeAsync([target, spared, Guid.CreateVersion7()], Now, CancellationToken.None))
                .Should().Be(2);
            (await new OAuthTokenRepository(unit).RevokeRefreshTokensOfGrantsAsync([target], Now, CancellationToken.None))
                .Should().Be(1);

            (await GetAsync(target)).RevokedAt.Should().BeNull("nothing is saved until the unit's owner commits");

            await unit.CommitAsync(token => unit.Context.SaveChangesAsync(token), CancellationToken.None);
        }

        (await GetAsync(target)).RevokedAt.Should().Be(Now);
        (await GetAsync(spared)).RevokedAt.Should().Be(Now);
        (await tokens.GetRefreshTokenAsync(live, CancellationToken.None))!.RevokedAt.Should().Be(Now);
        (await tokens.GetRefreshTokenAsync(rotated, CancellationToken.None))!.RevokedAt
            .Should().Be(Now.AddMinutes(-1), "a rotation's stamp is not overwritten");
        (await tokens.GetRefreshTokenAsync(sparedToken, CancellationToken.None))!.RevokedAt
            .Should().BeNull("only the grants named lose their tokens");
    }

    [Fact]
    public async Task TheTokenRevokeRunsOnlyOnAUnitAndAUnitCannotPinAnotherTenant()
    {
        var overFactory = () => Tokens().RevokeRefreshTokensOfGrantsAsync([Guid.CreateVersion7()], Now, CancellationToken.None);
        await overFactory.Should().ThrowAsync<InvalidOperationException>();

        await using var unit = DbUnitOfWork.Own(_db.CreateContext());
        var pinned = () => new OAuthGrantRepository(unit).GetStateAsync(_tenant, Guid.CreateVersion7(), CancellationToken.None);
        await pinned.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TheDirectListReadsWhatItsCallersShow()
    {
        var expired = await CreateAsync(Direct(_member, "h1", expiresAt: Now.AddDays(-1)));
        var live = await CreateAsync(Direct(_member, "h2"));
        var revoked = await CreateAsync(Direct(_member, "h3"));
        await CreateAsync(Direct(_stranger, "h4"));
        await Grants().RevokeAsync([revoked], Now, CancellationToken.None);

        (await Grants().ListDirectAsync(_member, liveAt: null, CancellationToken.None)).Select(g => g.Id)
            .Should().Equal(live, expired);
        (await Grants().ListDirectAsync(null, liveAt: null, CancellationToken.None)).Should().HaveCount(3);
        (await Grants().ListDirectAsync(_member, Now, CancellationToken.None)).Select(g => g.Id)
            .Should().Equal(live);
        (await GetAsync(revoked)).IsRevoked.Should().BeTrue();
        (await Grants().GetActiveForSubjectAsync(_member, CancellationToken.None)).Should().HaveCount(2);
        (await Grants().HasUnrevokedLegacySecretAsync("none", CancellationToken.None)).Should().BeFalse();
        (await Grants().GetTokenHashesAsync(CancellationToken.None)).Should().BeEquivalentTo(["h1", "h2", "h3", "h4"]);
        (await Grants().HasLiveShareGrantAsync(Now, CancellationToken.None)).Should().BeFalse();

        await using var check = _db.CreateContext();
        (await check.OAuthGrants.CountAsync()).Should().Be(4);
    }
}
