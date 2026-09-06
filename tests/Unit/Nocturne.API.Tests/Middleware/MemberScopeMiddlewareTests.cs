using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Middleware;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.API.Extensions;

namespace Nocturne.API.Tests.Middleware;

public class MemberScopeMiddlewareTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    [Fact]
    public async Task ApiKey_WithScopedGrant_DoesNotGetSuperuserAccess()
    {
        // An owner's api-secret grant scoped to glucose.read only. The owner's "*" membership must
        // not widen it back to superuser.
        var context = await ResolveAsync(OwnerPermissions, [Scope.GlucoseRead], AuthType.ApiKey);

        // Assert: should NOT have superuser wildcard
        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().Contain("glucose.read");
        grantedScopes.Should().NotContain("*");
        grantedScopes.Should().NotContain("treatments.readwrite");

        var permissionTrie = context.GetPermissionTrie();
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("api:entries:read").Should().BeTrue();
        permissionTrie.Check("api:treatments:read").Should().BeFalse();
        permissionTrie.Check("*").Should().BeFalse();
    }

    [Fact]
    public async Task ApiKey_WithFullAccessScope_GetsSuperuserAccess()
    {
        // An owner's full-access api-secret — what every uploader configured by the tenant owner
        // carries. Nothing is stripped, so DELETE (RequireScope("*")) keeps working.
        var context = await ResolveAsync(OwnerPermissions, [Scope.FullAccess], AuthType.ApiKey);

        // Assert: full access normalizes to all scopes
        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().Contain("*");

        var permissionTrie = context.GetPermissionTrie();
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("*").Should().BeTrue();
    }

    [Fact]
    public async Task InstanceKey_AlwaysGetsSuperuserAccess()
    {
        var (middleware, context) = Build(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.InstanceKey,
            SubjectId = _subjectId,
            TenantId = _tenantId,
            Scopes = [], // InstanceKey doesn't carry scopes
        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert: always superuser regardless of scopes
        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().Contain("*");

        var permissionTrie = context.GetPermissionTrie();
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("*").Should().BeTrue();
    }

    [Fact]
    public async Task PlatformAccess_AlwaysGetsSuperuserAccess()
    {
        // A platform-admin tenant-access grant (verified + tenant-pinned by
        // PlatformAccessCookieHandler) gets full superuser on the granted tenant,
        // with no membership lookup.
        var (middleware, context) = Build(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.PlatformAccess,
            SubjectId = _subjectId,
            TenantId = _tenantId,
            Scopes = [],
        });

        await middleware.InvokeAsync(context);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().Contain("*");

        var permissionTrie = context.GetPermissionTrie();
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("*").Should().BeTrue();
    }

    [Fact]
    public async Task ApiKey_WithMultipleScopes_GrantsOnlyThoseScopes()
    {
        var context = await ResolveAsync(
            OwnerPermissions, [Scope.GlucoseRead, Scope.TreatmentsReadWrite], AuthType.ApiKey);

        // Assert
        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().Contain("glucose.read");
        grantedScopes.Should().Contain("treatments.readwrite");
        grantedScopes.Should().NotContain("*");
        grantedScopes.Should().NotContain("therapy.read");

        var permissionTrie = context.GetPermissionTrie();
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("api:entries:read").Should().BeTrue();
        permissionTrie.Check("api:treatments:read").Should().BeTrue();
        permissionTrie.Check("api:treatments:create").Should().BeTrue();
        permissionTrie.Check("api:profile:read").Should().BeFalse();
    }

    [Fact]
    public async Task TenantMemberWithWildcardRole_GetsSuperuserPermissionTrie()
    {
        // Arrange — a tenant owner whose membership role grants "*", but whose session token
        // carries no scopes. Session tokens are minted from the subject's GLOBAL roles,
        // which are empty for a normal owner (their access comes from tenant membership), so
        // AuthenticationMiddleware leaves an empty PermissionTrie. The superuser branch must
        // rebuild it, or HasPermissions-gated endpoints (the legacy v1 API, e.g. the realtime
        // /api/v1/entries probe) would 403 for the owner on their own tenant.
        using var db = TestDbContextFactory.CreateSqlite();

        // Seeds the default tenant with TestSubjectId as owner (the "*" wildcard role).
        using (var seed = db.CreateContext())
        {
            TestDatabaseSeeder.Seed(seed);
        }

        // Act
        var context = await ResolveAsync(
            db, TestDatabaseSeeder.TestSubjectId, [], AuthType.SessionCookie);

        // Assert — a non-empty wildcard trie so the HasPermissions policy succeeds.
        var permissionTrie = context.GetPermissionTrie();
        permissionTrie.Should().NotBeNull();
        permissionTrie!.IsEmpty.Should().BeFalse();
        permissionTrie.Check("*").Should().BeTrue();

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes!.Should().Contain("*");
    }

    [Fact]
    public async Task OwnerMember_WithNarrowlyScopedOAuthToken_KeepsOnlyTheTokenScopes()
    {
        // A tenant owner who authorized a third-party app for glucose.read only. The owner's
        // membership grants "*", but the access token is the consent boundary: widening to
        // superuser here would hand the app write/delete on every resource.
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Owner], [Scope.GlucoseRead]);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().Contain(Scope.GlucoseRead);
        grantedScopes.Should().NotContain("*");
        grantedScopes.Should().NotContain(Scope.TreatmentsReadWrite);
        grantedScopes.Should().NotContain(Scope.TherapyReadWrite);

        var permissionTrie = context.GetPermissionTrie();
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("api:entries:read").Should().BeTrue();
        permissionTrie.Check("api:entries:create").Should().BeFalse();
        permissionTrie.Check("api:profile:update").Should().BeFalse();
        permissionTrie.Check("*").Should().BeFalse();
    }

    [Fact]
    public async Task OwnerMember_WithFullAccessOAuthToken_StillGetsSuperuser()
    {
        // The same owner consenting to full access keeps superuser: the credential's own scope
        // list is what bounds it, and "*" bounds nothing.
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Owner], [Scope.FullAccess]);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().Contain("*");

        var permissionTrie = context.GetPermissionTrie();
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("*").Should().BeTrue();
    }

    [Fact]
    public async Task OwnerMember_WithNarrowlyScopedDirectGrant_KeepsOnlyTheTokenScopes()
    {
        // Same token, presented as a bearer/?token= direct grant instead of an OAuth JWT. It must
        // resolve to the same narrow scopes it does in the api-secret header (AuthType.ApiKey).
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Owner], [Scope.GlucoseRead], AuthType.DirectGrant);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().BeEquivalentTo([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task NonOwnerMember_WithDeviceScopedToken_RetainsDeviceScopes()
    {
        // A caretaker running the desktop Companion: the OAuth token carries the device
        // capability scopes, and the caretaker seed role grants the matching permission atoms,
        // so the scope intersection must keep them (they'd otherwise 403 on
        // POST /api/v4/client-devices).
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Caretaker], CompanionTokenScopes);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().Contain(Scope.DeviceNotify);
        grantedScopes.Should().Contain(Scope.DeviceActuate);
        // Role atoms the token didn't request stay excluded.
        grantedScopes.Should().NotContain(Scope.TreatmentsReadWrite);
        grantedScopes.Should().NotContain("*");
    }

    [Fact]
    public async Task StaleSeedRole_WithoutDeviceAtoms_StillGrantsDeviceScopesFromToken()
    {
        // A tenant seeded before device.notify/device.actuate existed: its persisted caretaker
        // role row lacks the atoms and SeedRolesForTenantAsync never reconciles existing slugs.
        // The scopes are member-personal (the member's own client devices, not patient data), so
        // the middleware must grant them from the token alone — otherwise every pre-existing
        // tenant's members 403 on the client-devices API forever.
        var staleCaretakerPermissions = RoleSeeds
            .Permissions[RoleSeeds.Caretaker]
            .Where(p => !Scope.MemberPersonalScopes.Contains(p))
            .ToList();

        var context = await ResolveAsync(staleCaretakerPermissions, CompanionTokenScopes);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().Contain(Scope.DeviceNotify);
        grantedScopes.Should().Contain(Scope.DeviceActuate);
        // The role intersection still applies to everything else.
        grantedScopes.Should().Contain(Scope.GlucoseRead);
        grantedScopes.Should().NotContain(Scope.TreatmentsReadWrite);
        grantedScopes.Should().NotContain("*");
    }

    [Fact]
    public async Task ZeroPermissionMember_WithDeviceScopedToken_DoesNotGetDeviceScopes()
    {
        // The Denied seed role grants nothing. Device scopes must not bypass that: alert
        // actuations reveal patient state, so a member with no permissions at all gets none.
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Denied], CompanionTokenScopes);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AuthType.SessionCookie)]
    [InlineData(AuthType.LegacyJwt)]
    [InlineData(AuthType.LegacyAccessToken)]
    public async Task UnscopedCredential_ForAdminMember_ResolvesTheRoleIncludingAdministration(
        AuthType authType)
    {
        // The real web-app credential shape: no scopes at all, because SessionCookieHandler and
        // AccessTokenHandler never set them and a JWT reaching LegacyJwtHandler has no scope claim
        // (OAuthAccessTokenHandler claims those first). Intersecting membership against that empty
        // set 403ed the whole scope-gated surface for every non-owner. Every administration gate
        // (MemberInviteController, RoleController, ShareLinkController, GuestLinkController,
        // AuditController) reads GrantedScopes through Scope.Satisfies.
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Admin], [], authType);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().NotBeEmpty();

        foreach (var atom in new[]
                 {
                     Scope.MembersManage, Scope.MembersInvite,
                     Scope.RolesManage, Scope.TenantSettings,
                     Scope.SharingManage, Scope.SharingGuest,
                     Scope.AuditRead, Scope.GlucoseReadWrite,
                 })
        {
            Scope.Satisfies(grantedScopes!, atom).Should().BeTrue($"'{atom}' is granted");
        }

        // An Administrator is not a superuser, and audit.manage is Owner-only by design.
        grantedScopes.Should().NotContain(Scope.FullAccess);
        Scope.Satisfies(grantedScopes!, Scope.AuditManage)
            .Should().BeFalse();
    }

    [Fact]
    public async Task InteractiveOidcLogin_IsNotBoundedByTheProvidersProtocolScopes()
    {
        // OidcTokenHandler sets Scopes to the provider's configured scopes — openid/profile/email,
        // an outbound protocol list identical for every user of that provider, not a Nocturne data
        // grant. Normalize drops all three, so treating them as a ceiling resolved the member to
        // nothing.
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Caretaker],
            ["openid", "profile", "email"],
            AuthType.OidcToken);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().Contain(Scope.GlucoseRead);
        grantedScopes.Should().Contain(Scope.TreatmentsReadWrite);
        // The IdP's protocol scopes are not Nocturne scopes and must not be published.
        grantedScopes.Should().NotContain("openid");
        grantedScopes.Should().NotContain("profile");
    }

    [Fact]
    public async Task UnscopedCredential_RebuildsThePermissionTrieForLegacyEndpoints()
    {
        // The trie drives the HasPermissions policy on v1/v2/v3. An empty resolved scope set left it
        // empty for every non-owner web-app user.
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Caretaker], [], AuthType.SessionCookie);

        var permissionTrie = context.GetPermissionTrie();
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("api:entries:read").Should().BeTrue();
        permissionTrie.Check("api:treatments:create").Should().BeTrue();
        // Caretaker holds glucose.read, not glucose.readwrite.
        permissionTrie.Check("api:entries:create").Should().BeFalse();
        permissionTrie.Check("*").Should().BeFalse();
    }

    [Fact]
    public async Task UnscopedCredential_WithDeniedRole_ResolvesToNothing()
    {
        // An unscoped credential removes the ceiling, not the membership check.
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Denied], [], AuthType.SessionCookie);

        context.Items.Should().ContainKey("GrantedScopes",
            "the middleware has to resolve a scope set, not leave the request unscoped");
        (context.GetGrantedScopes()).Should().BeEmpty();
        (context.GetPermissionTrie())!.IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.ApiKey)]
    public async Task ScopedCredential_ForAdminMember_StaysBoundedByTheGrant(AuthType authType)
    {
        // An OAuth access token, a direct grant and an api-secret header all carry a consent
        // boundary, so an Admin membership must not widen past the scopes the credential presents
        // — administration included, since no client can request an administration atom.
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Admin], [Scope.GlucoseReadWrite], authType);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().BeEquivalentTo([Scope.GlucoseReadWrite]);
        Scope.Satisfies(grantedScopes!, Scope.MembersManage)
            .Should().BeFalse();
    }

    [Fact]
    public async Task ScopedCredential_ForReadWriteMember_DowngradesToTheGrantedReadScope()
    {
        // A read-only app authorized by a Caretaker: the member holds treatments.readwrite and the
        // token grants treatments.read. SatisfiesScope answers false for the readwrite requirement
        // and normalization adds no read counterpart, so this resolved to NEITHER scope.
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Caretaker],
            [Scope.TreatmentsRead],
            AuthType.OAuthAccessToken);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().BeEquivalentTo([Scope.TreatmentsRead]);
        grantedScopes.Should().NotContain(Scope.TreatmentsReadWrite);

        var permissionTrie = context.GetPermissionTrie();
        permissionTrie!.Check("api:treatments:read").Should().BeTrue();
        permissionTrie.Check("api:treatments:create").Should().BeFalse();
    }

    [Fact]
    public async Task GuestCredential_ForAdminMember_KeepsOnlyTheGuestLinkScopes()
    {
        // A guest link carries its own read-only scopes and never reaches the membership lookup.
        // Membership must not widen it even when the guest code was activated by a subject who is
        // also an Admin member of the tenant.
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Admin], [Scope.GlucoseRead], AuthType.Guest);

        (context.GetGrantedScopes())
            .Should().BeEquivalentTo([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task UnauthenticatedShareRequest_IsLeftUntouched()
    {
        // The public share path resolves its scopes in AuthenticationMiddleware with
        // IsAuthenticated false, so the Public membership never reaches this middleware. A share can
        // therefore never be widened by membership resolution, whatever atoms the Public subject
        // carries.
        var publicScopes = (IReadOnlySet<string>)new HashSet<string> { Scope.GlucoseRead };

        var middleware = new MemberScopeMiddleware(_ => Task.CompletedTask, NullLogger<MemberScopeMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = false,
            AuthType = AuthType.None,
            TenantId = TestDatabaseSeeder.TenantId,
        };
        context.Items["GrantedScopes"] = publicScopes;

        await middleware.InvokeAsync(context);

        context.Items["GrantedScopes"].Should().BeSameAs(publicScopes);
    }

    [Theory]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.DirectGrant)]
    public async Task ViewerMember_WithTreatmentsWriteGrant_LosesTheWriteScope(AuthType authType)
    {
        // A Viewer holding a treatments.readwrite direct grant. The Viewer seed role carries no
        // treatments atom at all, so the grant must not authorize treatment writes — whether the
        // token arrives in the api-secret header (ApiKey) or as Authorization: Bearer / ?token=
        // (DirectGrant). Before this, the header path returned the grant verbatim and the write
        // went through.
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Viewer], [Scope.TreatmentsReadWrite], authType);

        // The Viewer's own scopes (glucose.read, reports.read) are not in the grant either, so the
        // intersection is empty.
        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().BeEmpty();

        var permissionTrie = context.GetPermissionTrie();
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("api:treatments:create").Should().BeFalse();
    }

    [Theory]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.DirectGrant)]
    public async Task AdminMember_WithFullAccessGrant_DoesNotKeepTheDeleteScope(AuthType authType)
    {
        // The admin seed role has no "*" atom, so an admin cannot delete through the web UI. A
        // full-access grant they minted resolves the same way: the delete endpoints
        // (RequireScope("*") on DELETE /api/v1|v3/treatments/{id} and friends) stay closed to them.
        // Writes — every uploader's actual traffic — are untouched.
        var context = await ResolveAsync(
            RoleSeeds.Permissions[RoleSeeds.Admin], [Scope.FullAccess], authType);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().NotContain(Scope.FullAccess);
        grantedScopes.Should().Contain(Scope.GlucoseReadWrite);
        grantedScopes.Should().Contain(Scope.TreatmentsReadWrite);
        grantedScopes.Should().Contain(Scope.DevicesReadWrite);
    }

    [Fact]
    public async Task SameGrant_ViaApiSecretHeaderAndBearerToken_ResolvesToTheSameScopes()
    {
        // One direct grant row carries both a TokenHash (Bearer / ?token=) and a LegacySecretHash
        // (the SHA-1 api-secret header the field uploaders send), so the same credential reaches
        // MemberScopeMiddleware as either AuthType. Both must resolve identically, or the
        // presentation format picks the privilege level.
        var grantScopes = new List<string> { Scope.TreatmentsReadWrite, Scope.GlucoseReadWrite };

        foreach (var rolePermissions in new[]
                 {
                     OwnerPermissions,
                     RoleSeeds.Permissions[RoleSeeds.Admin],
                     RoleSeeds.Permissions[RoleSeeds.Caretaker],
                     RoleSeeds.Permissions[RoleSeeds.Viewer],
                 })
        {
            var headerScopes = (await ResolveAsync(rolePermissions, grantScopes, AuthType.ApiKey))
                .GetGrantedScopes();
            var bearerScopes = (await ResolveAsync(rolePermissions, grantScopes, AuthType.DirectGrant))
                .GetGrantedScopes();

            headerScopes.Should().BeEquivalentTo(
                bearerScopes,
                "the api-secret header and the bearer token are the same grant");
        }
    }

    [Theory]
    [InlineData(RoleSeeds.Caretaker)]
    [InlineData(RoleSeeds.Clinician)]
    public async Task NonWritingRole_WithFullAccessApiSecret_LosesTheUploadScopes(string roleSlug)
    {
        // The highest-impact narrowing in this change, pinned so it is a decision rather than a
        // surprise. Caretaker holds glucose.read and devices.read — not the readwrite counterparts
        // — and Clinician holds no write atom at all. A full-access api-secret minted by either
        // therefore stops satisfying [RequireScope(GlucoseReadWrite)] on POST /api/v1/entries and
        // [RequireScope(DevicesReadWrite)] on POST /api/v1/devicestatus, which is CGM and loop
        // uploader traffic. Their treatments writes are unaffected for Caretaker, which holds
        // treatments.readwrite.
        //
        // No such grant exists in production today (every direct grant is owner-held), so this is
        // latent rather than a live regression, but minting a key as a Caretaker would hit it.
        var context = await ResolveAsync(
            RoleSeeds.Permissions[roleSlug], [Scope.FullAccess], AuthType.ApiKey);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().NotContain(Scope.GlucoseReadWrite);
        grantedScopes.Should().NotContain(Scope.DevicesReadWrite);
        grantedScopes.Should().Contain(Scope.GlucoseRead, "reads are retained");

        var permissionTrie = context.GetPermissionTrie();
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("api:entries:create").Should().BeFalse();
        permissionTrie.Check("api:entries:read").Should().BeTrue();
    }

    [Fact]
    public async Task MigratedNightscoutGrant_OnOwnerMembership_KeepsEveryUploadScope()
    {
        // ConnectorConfigurationService seeds the migrated Nightscout secret with
        // Normalize([health.readwrite]) against the subject who saved the connector secrets.
        // That subject is a tenant member, so the grant is now intersected — the eight
        // health.readwrite scopes every field uploader writes with must survive.
        var migratedGrantScopes = Scope.Normalize([Scope.HealthReadWrite]).ToList();

        var context = await ResolveAsync(OwnerPermissions, migratedGrantScopes, AuthType.ApiKey);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().Contain(Scope.HealthReadWriteExpansion);

        var permissionTrie = context.GetPermissionTrie();
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("api:entries:create").Should().BeTrue();
        permissionTrie.Check("api:treatments:create").Should().BeTrue();
        permissionTrie.Check("api:devicestatus:create").Should().BeTrue();
    }

    [Fact]
    public async Task ApiKey_WithNoMembershipRow_KeepsTheGrantScopesAndTrie()
    {
        // AuthenticationMiddleware exempts AuthType.ApiKey from the tenant-membership check, so an
        // api-secret grant minted against a subject with no membership on this tenant (a platform
        // admin who configured the connector, a member since removed) reaches the middleware with
        // membership == null. It keeps the grant's own scopes rather than resolving to nothing.
        //
        // The trie is asserted alongside the scopes because they are separate carriers.
        // ApiKeyHandler leaves AuthContext.Permissions empty, so the trie AuthenticationMiddleware
        // builds is empty, and PolicyNames.HasPermissions — class-level on every V1/V2/V3
        // controller — succeeds only on a non-empty trie. Scopes alone passing is not enough to
        // keep the legacy uploader surface reachable.
        using var db = TestDbContextFactory.CreateSqlite();

        using (var seed = db.CreateContext())
        {
            TestDatabaseSeeder.Seed(seed);
        }

        var context = await ResolveAsync(
            db, Guid.CreateVersion7(), [Scope.TreatmentsReadWrite], AuthType.ApiKey);

        var grantedScopes = context.GetGrantedScopes();
        grantedScopes.Should().Contain(Scope.TreatmentsReadWrite);

        var permissionTrie = context.GetPermissionTrie();
        permissionTrie.Should().NotBeNull();
        permissionTrie!.Check("api:treatments:create").Should().BeTrue(
            "HasPermissionsHandler succeeds only on a non-empty trie");
        permissionTrie.Check("api:entries:create").Should().BeFalse(
            "the grant carries treatments.readwrite only");
    }

    /// <summary>
    /// Seeds a member holding <paramref name="rolePermissions"/> on a database of its own, then
    /// runs the middleware for a credential carrying <paramref name="tokenScopes"/>.
    /// </summary>
    private static async Task<DefaultHttpContext> ResolveAsync(
        List<string> rolePermissions,
        List<string> tokenScopes,
        AuthType authType = AuthType.OAuthAccessToken)
    {
        using var db = TestDbContextFactory.CreateSqlite();
        var subjectId = SeedMemberWithRole(db, rolePermissions);
        return await ResolveAsync(db, subjectId, tokenScopes, authType);
    }

    /// <summary>
    /// Runs the middleware over <paramref name="db"/> for a request authenticated as
    /// <paramref name="authType"/> whose credential carries <paramref name="tokenScopes"/>, as
    /// AuthenticationMiddleware would leave it, and returns the request it resolved. The service provider is disposed before returning, so the
    /// caller asserts on <c>HttpContext.Items</c> alone.
    /// </summary>
    private static async Task<DefaultHttpContext> ResolveAsync(
        SqliteTestDatabase db,
        Guid subjectId,
        List<string> tokenScopes,
        AuthType authType)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => db.CreateContext());
        using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = authType,
            SubjectId = subjectId,
            TenantId = TestDatabaseSeeder.TenantId,
            Scopes = tokenScopes,
        };
        context.Items["GrantedScopes"] = Scope.Normalize(tokenScopes);
        context.Items["PermissionTrie"] = new PermissionTrie();

        var middleware = new MemberScopeMiddleware(
            _ => Task.CompletedTask, NullLogger<MemberScopeMiddleware>.Instance);
        await middleware.InvokeAsync(context);

        return context;
    }

    /// <summary>The owner seed role's permissions (the "*" wildcard).</summary>
    private static List<string> OwnerPermissions =>
        RoleSeeds.Permissions[RoleSeeds.Owner];

    /// <summary>The desktop Companion's device-flow token scopes.</summary>
    private static readonly List<string> CompanionTokenScopes =
    [
        Scope.GlucoseRead, Scope.TherapyRead, Scope.DevicesRead,
        Scope.DeviceNotify, Scope.DeviceActuate,
    ];

    /// <summary>
    /// Seeds the default tenant plus a non-owner member holding a single role with the given
    /// permission atoms. Returns the member's subject id.
    /// </summary>
    private static Guid SeedMemberWithRole(
        SqliteTestDatabase db, List<string> rolePermissions)
    {
        var subjectId = Guid.CreateVersion7();
        using var seed = db.CreateContext();
        TestDatabaseSeeder.Seed(seed);

        seed.Subjects.Add(new Nocturne.Infrastructure.Data.Entities.SubjectEntity
        {
            Id = subjectId,
            Name = "Member",
            IsActive = true,
            IsSystemSubject = false,
        });
        var memberId = Guid.CreateVersion7();
        seed.TenantMembers.Add(new Nocturne.Infrastructure.Data.Entities.TenantMemberEntity
        {
            Id = memberId,
            TenantId = TestDatabaseSeeder.TenantId,
            SubjectId = subjectId,
        });
        var roleId = Guid.CreateVersion7();
        seed.TenantRoles.Add(new Nocturne.Infrastructure.Data.Entities.TenantRoleEntity
        {
            Id = roleId,
            TenantId = TestDatabaseSeeder.TenantId,
            Name = "Member Role",
            Slug = "member-role",
            Permissions = rolePermissions,
            IsSystem = true,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });
        seed.TenantMemberRoles.Add(new Nocturne.Infrastructure.Data.Entities.TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = memberId,
            TenantRoleId = roleId,
            SysCreatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
        return subjectId;
    }

    private (MemberScopeMiddleware middleware, DefaultHttpContext context) Build(AuthContext authContext)
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var middleware = new MemberScopeMiddleware(next, NullLogger<MemberScopeMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Items["AuthContext"] = authContext;

        return (middleware, context);
    }
}
