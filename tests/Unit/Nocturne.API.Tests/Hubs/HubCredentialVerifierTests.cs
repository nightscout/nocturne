using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Hubs;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// A hub credential is proven again from the records that keep it valid, never from the lifetime of
/// the access token it was presented with: a live grant keeps an expired token's connection, while
/// a revoked or expired grant, a removed or narrowed membership, a new history clamp, a deactivated
/// subject or a logged-out session withdraws it.
/// </summary>
[Trait("Category", "Unit")]
public class HubCredentialVerifierTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly TenantContext TenantContext = new(Tenant, "default", "Default", IsActive: true, IsDemo: false);

    private sealed class SharedInMemoryFactory(string dbName) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext()
        {
            var ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
            ctx.TenantId = Tenant;
            return ctx;
        }
    }

    private readonly IDbContextFactory<NocturneDbContext> _contexts =
        new SharedInMemoryFactory($"hub_verifier_{Guid.NewGuid()}");

    private readonly Guid _subject = Guid.NewGuid();
    private readonly Guid _grant = Guid.NewGuid();
    private readonly Mock<ITenantMemberService> _members = new();
    private readonly Mock<IGuestLinkService> _guestLinks = new();
    private readonly Mock<ITenantOverviewService> _overview = new();
    private readonly Mock<IOAuthTokenRevocationCache> _revokedTokens = new();

    private static readonly IReadOnlySet<string> MemberPermissions =
        new HashSet<string> { Scope.GlucoseRead, Scope.AlertsRead };

    public HubCredentialVerifierTests()
    {
        Member(MemberPermissions, limitTo24Hours: false);
    }

    private void Member(IReadOnlySet<string>? permissions, bool limitTo24Hours) =>
        _members
            .Setup(m => m.GetMemberAccessAsync(_subject, Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(permissions is null ? null : new TenantMemberAccess(permissions, limitTo24Hours));

    private async Task SeedAsync(
        bool subjectActive = true,
        bool platformAdmin = false,
        DateTime? grantRevokedAt = null,
        DateTime? grantExpiresAt = null,
        params string?[] sessions)
    {
        await using var db = await _contexts.CreateDbContextAsync();
        db.Tenants.Add(new TenantEntity { Id = Tenant, Slug = "default", DisplayName = "Default", IsActive = true });
        db.Subjects.Add(new SubjectEntity
        {
            Id = _subject, Name = "Member", IsActive = subjectActive, IsPlatformAdmin = platformAdmin,
        });
        db.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = _grant,
            TenantId = Tenant,
            SubjectId = _subject,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = [Scope.GlucoseRead, Scope.AlertsRead],
            RevokedAt = grantRevokedAt,
            ExpiresAt = grantExpiresAt,
        });
        foreach (var session in sessions)
        {
            db.RefreshTokens.Add(new RefreshTokenEntity
            {
                Id = Guid.NewGuid(), SubjectId = _subject, OidcSessionId = session, TokenHash = $"hash-{session}",
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
        }
        await db.SaveChangesAsync();
    }

    private Task SignOutAsync(string session) =>
        UpdateAsync(db => db.RefreshTokens.Single(t => t.OidcSessionId == session).RevokedAt = DateTime.UtcNow);

    private async Task UpdateAsync(Action<NocturneDbContext> change)
    {
        await using var db = await _contexts.CreateDbContextAsync();
        change(db);
        await db.SaveChangesAsync();
    }

    private HubCredentialVerifier Verifier()
    {
        var services = new ServiceCollection()
            .AddSingleton(_contexts.AsNocturneFactory())
            .AddSingleton(_members.Object)
            .AddSingleton(_guestLinks.Object)
            .AddSingleton(_overview.Object)
            .AddSingleton(_revokedTokens.Object)
            .AddScoped<ITenantAccessor, HttpContextTenantAccessor>()
            .BuildServiceProvider();
        return new HubCredentialVerifier(services, TimeProvider.System);
    }

    private HubAuthorization Credential(
        AuthType authType, Guid? grantId, HubCredentialKind kind = HubCredentialKind.Subject, TimeSpan? expiresIn = null,
        string? sessionId = null) =>
        new(Tenant, new HashSet<string>(), kind, authType is AuthType.Guest ? null : _subject, HistoryClamped: false)
        {
            Source = new HubCredentialSource(
                authType,
                authType is AuthType.Guest ? null : _subject,
                grantId,
                Scope.Normalize([Scope.GlucoseRead, Scope.AlertsRead]),
                LimitTo24Hours: false,
                ExpiresAt: DateTimeOffset.UtcNow + (expiresIn ?? TimeSpan.FromMinutes(-30)),
                sessionId),
        };

    /// <summary>The credential as the connection was admitted with it, proven from the seeded state.</summary>
    private async Task<HubAuthorization> AdmittedAsync(HubAuthorization credential)
    {
        var admitted = await Verifier().ProveAsync(credential, TenantContext);
        admitted.Should().NotBeNull();
        admitted!.Scopes.Should().NotBeEmpty();
        return admitted;
    }

    private async Task<bool> StillCoveredAsync(HubAuthorization admitted) =>
        admitted.IsCoveredBy(await Verifier().ProveAsync(admitted, TenantContext));

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.ApiKey)]
    public async Task A_live_grant_keeps_a_credential_whose_access_token_has_expired(AuthType authType)
    {
        await SeedAsync();
        var admitted = await AdmittedAsync(Credential(authType, _grant));

        (await StillCoveredAsync(admitted)).Should().BeTrue();
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.ApiKey)]
    public async Task A_revoked_grant_withdraws_the_credential(AuthType authType)
    {
        await SeedAsync();
        var admitted = await AdmittedAsync(Credential(authType, _grant));

        await UpdateAsync(db => db.OAuthGrants.Single().RevokedAt = DateTime.UtcNow);

        (await StillCoveredAsync(admitted)).Should().BeFalse();
    }

    [Theory]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.ApiKey)]
    public async Task An_expired_grant_withdraws_the_credential(AuthType authType)
    {
        await SeedAsync();
        var admitted = await AdmittedAsync(Credential(authType, _grant));

        await UpdateAsync(db => db.OAuthGrants.Single().ExpiresAt = DateTime.UtcNow.AddMinutes(-1));

        (await StillCoveredAsync(admitted)).Should().BeFalse();
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.LegacyJwt)]
    public async Task A_removed_member_loses_the_credential(AuthType authType)
    {
        await SeedAsync();
        var admitted = await AdmittedAsync(
            Credential(authType, authType is AuthType.LegacyJwt ? null : _grant, expiresIn: TimeSpan.FromMinutes(5)));

        Member(null, limitTo24Hours: false);

        (await StillCoveredAsync(admitted)).Should().BeFalse();
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.LegacyJwt)]
    [InlineData(AuthType.OidcToken)]
    public async Task A_token_with_no_grant_behind_it_lasts_only_as_long_as_the_token(AuthType authType)
    {
        await SeedAsync();
        var live = Credential(authType, null, expiresIn: TimeSpan.FromMinutes(5));
        var admitted = await AdmittedAsync(live);
        (await StillCoveredAsync(admitted)).Should().BeTrue();

        var lapsed = admitted with { Source = admitted.Source! with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) } };

        (await Verifier().ProveAsync(lapsed, TenantContext)).Should().BeNull();
    }

    [Theory]
    [InlineData(AuthType.LegacyJwt)]
    [InlineData(AuthType.OAuthAccessToken)]
    public async Task A_token_minted_from_a_grant_ends_when_the_grant_is_revoked_or_expires(AuthType authType)
    {
        await SeedAsync();
        var admitted = await AdmittedAsync(Credential(authType, _grant));

        await UpdateAsync(db => db.OAuthGrants.Single().ExpiresAt = DateTime.UtcNow.AddMinutes(-1));
        (await StillCoveredAsync(admitted)).Should().BeFalse();

        await UpdateAsync(db =>
        {
            var grant = db.OAuthGrants.Single();
            grant.ExpiresAt = null;
            grant.RevokedAt = DateTime.UtcNow;
        });
        (await StillCoveredAsync(admitted)).Should().BeFalse();
    }

    [Fact]
    public async Task An_api_secret_needs_no_membership_and_keeps_its_grants_scopes()
    {
        await SeedAsync();
        Member(null, limitTo24Hours: false);
        var admitted = await AdmittedAsync(Credential(AuthType.ApiKey, _grant));

        (await StillCoveredAsync(admitted)).Should().BeTrue();
    }

    [Fact]
    public async Task A_narrowed_membership_withdraws_the_credential()
    {
        await SeedAsync();
        var admitted = await AdmittedAsync(Credential(AuthType.OAuthAccessToken, _grant));

        Member(new HashSet<string> { Scope.GlucoseRead }, limitTo24Hours: false);

        (await StillCoveredAsync(admitted)).Should().BeFalse();
    }

    [Fact]
    public async Task A_new_history_clamp_withdraws_the_credential()
    {
        await SeedAsync();
        var admitted = await AdmittedAsync(Credential(AuthType.OAuthAccessToken, _grant));

        Member(MemberPermissions, limitTo24Hours: true);

        (await StillCoveredAsync(admitted)).Should().BeFalse();
    }

    [Fact]
    public async Task A_deactivated_subject_loses_the_credential()
    {
        await SeedAsync();
        var admitted = await AdmittedAsync(Credential(AuthType.OAuthAccessToken, _grant));

        await UpdateAsync(db => db.Subjects.Single().IsActive = false);

        (await StillCoveredAsync(admitted)).Should().BeFalse();
    }

    [Fact]
    public async Task A_session_login_lasts_while_the_subject_has_a_session()
    {
        await SeedAsync(sessions: "laptop");
        var admitted = await AdmittedAsync(Credential(AuthType.SessionCookie, null, sessionId: "laptop"));
        (await StillCoveredAsync(admitted)).Should().BeTrue();

        await SignOutAsync("laptop");

        (await StillCoveredAsync(admitted)).Should().BeFalse();
    }

    [Fact]
    public async Task Signing_out_one_session_ends_only_that_sessions_login()
    {
        await SeedAsync(sessions: ["laptop", "phone"]);
        var laptop = await AdmittedAsync(Credential(AuthType.SessionCookie, null, sessionId: "laptop"));
        var phone = await AdmittedAsync(Credential(AuthType.SessionCookie, null, sessionId: "phone"));

        await SignOutAsync("laptop");

        (await StillCoveredAsync(laptop)).Should().BeFalse();
        (await StillCoveredAsync(phone)).Should().BeTrue();
    }

    [Fact]
    public async Task A_session_login_without_its_session_id_lasts_while_the_subject_has_any_session()
    {
        await SeedAsync(sessions: ["laptop", "phone"]);
        var admitted = await AdmittedAsync(Credential(AuthType.SessionCookie, null));

        await SignOutAsync("laptop");
        (await StillCoveredAsync(admitted)).Should().BeTrue();

        await SignOutAsync("phone");
        (await StillCoveredAsync(admitted)).Should().BeFalse();
    }

    [Fact]
    public async Task A_guest_credential_lasts_while_its_guest_link_does()
    {
        await SeedAsync();
        _guestLinks
            .Setup(g => g.ValidateSessionAsync(_grant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestSessionInfo(_grant, Tenant, _subject, [Scope.GlucoseRead], "Link", DateTime.UtcNow.AddDays(1)));
        var admitted = await AdmittedAsync(Credential(AuthType.Guest, _grant, HubCredentialKind.Restricted));
        (await StillCoveredAsync(admitted)).Should().BeTrue();

        _guestLinks
            .Setup(g => g.ValidateSessionAsync(_grant, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GuestSessionInfo?)null);

        (await StillCoveredAsync(admitted)).Should().BeFalse();
    }

    [Fact]
    public async Task Platform_access_lasts_while_the_subject_is_a_platform_admin()
    {
        await SeedAsync(platformAdmin: true);
        var credential = Credential(AuthType.PlatformAccess, null) with
        {
            Scopes = new HashSet<string> { Scope.FullAccess },
            Source = Credential(AuthType.PlatformAccess, null).Source! with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) },
        };
        (await StillCoveredAsync(credential)).Should().BeTrue();

        await UpdateAsync(db => db.Subjects.Single().IsPlatformAdmin = false);

        (await StillCoveredAsync(credential)).Should().BeFalse();
    }

    [Fact]
    public async Task Platform_access_ends_when_its_token_is_revoked()
    {
        await SeedAsync(platformAdmin: true);
        var jti = Guid.NewGuid();
        var credential = Credential(AuthType.PlatformAccess, jti) with
        {
            Scopes = new HashSet<string> { Scope.FullAccess },
            Source = Credential(AuthType.PlatformAccess, jti).Source! with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) },
        };
        (await StillCoveredAsync(credential)).Should().BeTrue();

        _revokedTokens.Setup(r => r.IsRevokedAsync(jti.ToString(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        (await StillCoveredAsync(credential)).Should().BeFalse();
    }

    private HubAuthorization InstanceKeyCredential() =>
        new(Tenant, new HashSet<string> { Scope.FullAccess }, HubCredentialKind.Subject, null, HistoryClamped: false)
        {
            Source = new HubCredentialSource(
                AuthType.InstanceKey, null, null, new HashSet<string> { Scope.FullAccess }, false, null),
        };

    public static TheoryData<string> TenantRemovals => new() { "deactivated", "deleted" };

    [Theory]
    [MemberData(nameof(TenantRemovals))]
    public async Task A_tenant_that_is_deactivated_or_deleted_withdraws_every_credential_on_it(string removal)
    {
        await SeedAsync();
        var member = await AdmittedAsync(Credential(AuthType.OAuthAccessToken, _grant));
        var instanceKey = await AdmittedAsync(InstanceKeyCredential());

        await UpdateAsync(db =>
        {
            var tenant = db.Tenants.Single();
            if (removal == "deleted") db.Tenants.Remove(tenant);
            else tenant.IsActive = false;
        });

        (await StillCoveredAsync(member)).Should().BeFalse();
        (await StillCoveredAsync(instanceKey)).Should().BeFalse();
    }

    [Fact]
    public async Task A_credential_with_no_source_cannot_be_proven()
    {
        var credential = new HubAuthorization(
            Tenant, new HashSet<string> { Scope.GlucoseRead }, HubCredentialKind.Subject, _subject, HistoryClamped: false);

        (await Verifier().ProveAsync(credential, TenantContext)).Should().BeNull();
    }

    private OverviewGrant Overview(params Guid[] tenants) =>
        new(_subject, AuthType.OAuthAccessToken, new HashSet<string> { Scope.GlucoseRead }, tenants);

    private void Reaches(params Guid[] tenants) => Reaches(AuthType.OAuthAccessToken, tenants);

    private void Reaches(AuthType authType, params Guid[] tenants) =>
        _overview
            .Setup(o => o.GetGlucoseReadTenantsAsync(
                _subject, It.IsAny<IReadOnlySet<string>>(), authType, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenants
                .Select(id => new GlucoseReadTenant(new TenantEntity { Id = id }, new HashSet<string>(), false, new HashSet<string>()))
                .ToList());

    [Fact]
    public async Task An_overview_connection_lasts_while_it_reaches_every_tenant_it_receives()
    {
        await SeedAsync();
        var other = Guid.NewGuid();
        Reaches(Tenant, other);
        (await Verifier().StillHoldsAsync(Overview(Tenant, other))).Should().BeTrue();

        Reaches(Tenant);
        (await Verifier().StillHoldsAsync(Overview(Tenant, other))).Should().BeFalse();
    }

    [Fact]
    public async Task An_overview_connection_ends_with_its_subject()
    {
        await SeedAsync(subjectActive: false);
        Reaches(Tenant);

        (await Verifier().StillHoldsAsync(Overview(Tenant))).Should().BeFalse();
    }

    [Fact]
    public async Task Signing_out_one_session_ends_only_that_sessions_overview_connection()
    {
        await SeedAsync(sessions: ["laptop", "phone"]);
        Reaches(AuthType.SessionCookie, Tenant);
        var laptop = Overview(Tenant) with { AuthType = AuthType.SessionCookie, SessionId = "laptop" };
        var phone = laptop with { SessionId = "phone" };

        await SignOutAsync("laptop");

        (await Verifier().StillHoldsAsync(laptop)).Should().BeFalse();
        (await Verifier().StillHoldsAsync(phone)).Should().BeTrue();
    }
}
