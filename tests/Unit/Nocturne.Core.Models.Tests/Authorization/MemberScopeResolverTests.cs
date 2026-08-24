using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// Pins <see cref="MemberScopeResolver"/>, the single place a tenant membership is turned into a
/// granted scope set. Both <c>MemberScopeMiddleware</c> (per request) and
/// <c>TenantOverviewService</c> (per membership, for the tenant picker) route through it, so these
/// cases bind the access decision for the whole tenant surface.
/// </summary>
[Trait("Category", "Unit")]
public class MemberScopeResolverTests
{
    private static readonly IReadOnlySet<string> NoScopes = new HashSet<string>();

    /// <summary>The credential types that carry no scope grant, as the resolver must classify them.</summary>
    private static readonly AuthType[] ExpectedUnscopedTypes =
    [
        AuthType.SessionCookie,
        AuthType.OidcToken,
        AuthType.LegacyJwt,
        AuthType.LegacyAccessToken,
    ];

    private static IReadOnlySet<string> Resolve(
        IEnumerable<string> permissions, AuthType authType, params string[] credentialScopes)
    {
        return MemberScopeResolver.Resolve(
            new HashSet<string>(permissions), authType, new HashSet<string>(credentialScopes));
    }

    // ---- the discriminator ----

    [Fact]
    public void UnscopedCredentialTypes_IsExactlyTheCredentialsThatCarryNoGrant()
    {
        // A mutation here is the whole security boundary: adding a delegated type erases consent,
        // removing an unscoped type resolves that credential to nothing.
        MemberScopeResolver.UnscopedCredentialTypes.Should().BeEquivalentTo(ExpectedUnscopedTypes);
    }

    [Fact]
    public void EveryOtherAuthType_IsTreatedAsScoped()
    {
        // Fail closed: a newly declared AuthType is capped by the credential's scopes until someone
        // deliberately classifies it.
        var scoped = Enum.GetValues<AuthType>().Except(ExpectedUnscopedTypes);

        scoped.Should().OnlyContain(
            authType => !MemberScopeResolver.UnscopedCredentialTypes.Contains(authType));
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.Guest)]
    [InlineData(AuthType.InstanceKey)]
    [InlineData(AuthType.PlatformAccess)]
    [InlineData(AuthType.None)]
    public void ScopedCredential_WithNoScopes_ResolvesToNothing(AuthType authType)
    {
        // A credential that presents no scopes and is not classified as unscoped grants nothing,
        // however broad the membership.
        Resolve([Scope.FullAccess], authType).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(UnscopedTypes))]
    public void UnscopedCredential_WithNoScopes_ResolvesFromMembership(AuthType authType)
    {
        Resolve([Scope.GlucoseRead], authType)
            .Should().Contain(Scope.GlucoseRead);
    }

    public static TheoryData<AuthType> UnscopedTypes()
    {
        var data = new TheoryData<AuthType>();
        foreach (var authType in ExpectedUnscopedTypes)
            data.Add(authType);
        return data;
    }

    // ---- per-seed-role resolution on an unscoped credential ----

    [Theory]
    [MemberData(nameof(SeedRoleExpectations))]
    public void SeedRole_OnUnscopedCredential_ResolvesRoleScopesWithoutWidening(
        string roleSlug, AuthType authType, string[] expected, string[] notExpected)
    {
        // The real web-app credential shape: no scopes at all, because a session JWT's permission
        // claims are the subject's GLOBAL roles (empty for a member who joined by invite).
        // Intersecting against that resolved every non-owner role to zero scopes.
        var resolved = Resolve(
            RoleSeeds.Permissions[roleSlug], authType);

        foreach (var scope in expected)
            Scope.Satisfies(resolved, scope).Should().BeTrue($"'{scope}' is granted");
        foreach (var scope in notExpected)
            Scope.Satisfies(resolved, scope).Should().BeFalse($"'{scope}' is not granted");
    }

    /// <summary>
    /// Every seed role crossed with every unscoped credential type: the permissions the role must
    /// resolve to, and the permissions it must not reach. Asserted through
    /// <see cref="Scope.Satisfies"/> because that is the gate the administration and
    /// data controllers actually apply to <c>GrantedScopes</c>.
    /// </summary>
    public static TheoryData<string, AuthType, string[], string[]> SeedRoleExpectations()
    {
        (string Role, string[] Expected, string[] NotExpected)[] roles =
        [
            (
                RoleSeeds.Owner,
                // "*" satisfies every atom through Scope.Satisfies.
                [Scope.FullAccess, Scope.GlucoseReadWrite, Scope.AuditManage],
                []
            ),
            (
                RoleSeeds.Admin,
                [
                    Scope.GlucoseReadWrite, Scope.TreatmentsReadWrite,
                    Scope.TherapyReadWrite, Scope.FoodReadWrite,
                    Scope.AlertsReadWrite, Scope.DevicesReadWrite,
                    Scope.SleepReadWrite, Scope.ReportsRead,
                    Scope.MembersManage, Scope.MembersInvite,
                    Scope.TenantSettings, Scope.RolesManage,
                    Scope.SharingManage, Scope.SharingGuest,
                    Scope.AuditRead,
                    Scope.DeviceNotify, Scope.DeviceActuate,
                ],
                // audit.manage is Owner-only by design, and an Administrator is not a superuser.
                [Scope.FullAccess, Scope.AuditManage]
            ),
            (
                RoleSeeds.Clinician,
                [
                    Scope.GlucoseRead, Scope.TreatmentsRead,
                    Scope.TherapyRead, Scope.AlertsRead,
                    Scope.ReportsRead, Scope.SleepRead,
                ],
                [
                    Scope.FullAccess, Scope.GlucoseReadWrite,
                    Scope.TreatmentsReadWrite, Scope.TherapyReadWrite,
                    Scope.AlertsReadWrite,
                    Scope.MembersManage, Scope.TenantSettings,
                    Scope.AuditRead,
                ]
            ),
            (
                RoleSeeds.Caretaker,
                [
                    Scope.GlucoseRead, Scope.TreatmentsReadWrite,
                    Scope.TherapyRead, Scope.AlertsReadWrite,
                    Scope.FoodRead,
                ],
                [
                    Scope.FullAccess, Scope.GlucoseReadWrite,
                    Scope.TherapyReadWrite, Scope.FoodReadWrite,
                    Scope.MembersManage, Scope.TenantSettings,
                    Scope.AuditRead,
                ]
            ),
            (
                RoleSeeds.Viewer,
                [Scope.GlucoseRead, Scope.ReportsRead],
                [
                    Scope.FullAccess, Scope.GlucoseReadWrite,
                    Scope.TreatmentsRead, Scope.TherapyRead,
                    Scope.TenantSettings, Scope.MembersManage,
                ]
            ),
            (
                RoleSeeds.Denied,
                [],
                [
                    Scope.FullAccess, Scope.GlucoseRead,
                    Scope.ReportsRead, Scope.DeviceNotify,
                    Scope.DeviceActuate, Scope.MembersManage,
                ]
            ),
        ];

        var data = new TheoryData<string, AuthType, string[], string[]>();
        foreach (var (role, expected, notExpected) in roles)
        {
            foreach (var authType in ExpectedUnscopedTypes)
                data.Add(role, authType, expected, notExpected);
        }
        return data;
    }

    [Fact]
    public void DeniedRole_OnUnscopedCredential_ResolvesToNothing()
    {
        // An unscoped credential removes the ceiling, not the membership check. Zero permissions
        // means zero scopes, including the member-personal device capabilities — alert actuations
        // reveal patient state.
        Resolve(RoleSeeds.Permissions[RoleSeeds.Denied],
            AuthType.SessionCookie).Should().BeEmpty();
    }

    // ---- superuser ----

    [Fact]
    public void Superuser_OnUnscopedCredential_KeepsTheWildcard()
    {
        Resolve([Scope.FullAccess], AuthType.SessionCookie)
            .Should().Contain(Scope.FullAccess);
    }

    /// <summary>
    /// The superuser branch publishes the membership's permissions RAW rather than the normalized
    /// expansion. Asserting only that the wildcard survives is too weak to pin that: normalization
    /// re-adds the wildcard, so a resolver that normalized here would still pass such a check while
    /// changing the granted set from one atom to the whole expansion.
    /// </summary>
    [Fact]
    public void Superuser_OnUnscopedCredential_PublishesTheRawPermissionsNotTheExpansion()
    {
        Resolve([Scope.FullAccess], AuthType.SessionCookie)
            .Should().BeEquivalentTo([Scope.FullAccess],
                "the raw permission set is published, so the wildcard travels alone rather than "
                + "expanded into every atom it stands for");
    }

    /// <summary>
    /// The consequence that makes the raw publication load-bearing: normalizing a superuser set
    /// would route through the full-access short circuit, which expands to
    /// <see cref="Scope.AllScopes"/> — a set that excludes the tenant-administration atoms. A
    /// superuser would silently lose them.
    /// </summary>
    [Fact]
    public void Superuser_OnUnscopedCredential_KeepsAdministrationAtomsTheExpansionWouldDrop()
    {
        var resolved = Resolve(
            [Scope.FullAccess, Scope.MembersManage, Scope.AuditManage],
            AuthType.SessionCookie);

        resolved.Should().Contain(Scope.MembersManage);
        resolved.Should().Contain(Scope.AuditManage);
        Scope.AllScopes.Should().NotContain(Scope.MembersManage,
            "this is why the raw set has to be published rather than the expansion");
    }

    /// <summary>
    /// Scope atoms are lowercase and matched ordinally everywhere else in the vocabulary. A
    /// resolved set that compared case-insensitively would admit a scope string the vocabulary
    /// never defines, and set-equivalence assertions cannot see a comparer — so each of the three
    /// return paths needs its own case. The superuser branch is the least trafficked of them:
    /// every non-owner member and every scoped credential leaves by one of the other two.
    /// </summary>
    [Fact]
    public void ResolvedScopes_AreMatchedOrdinally_OnTheSuperuserPath()
    {
        var resolved = Resolve([Scope.FullAccess, Scope.MembersManage], AuthType.SessionCookie);

        resolved.Contains(Scope.MembersManage.ToUpperInvariant()).Should().BeFalse(
            "scope matching is ordinal, so an upper-cased atom is a different string");
    }

    [Fact]
    public void ResolvedScopes_AreMatchedOrdinally_OnTheUnscopedMemberPath()
    {
        var resolved = Resolve(
            [Scope.GlucoseReadWrite, Scope.MembersManage], AuthType.SessionCookie);

        resolved.Should().Contain(Scope.GlucoseReadWrite);
        resolved.Contains(Scope.GlucoseReadWrite.ToUpperInvariant()).Should().BeFalse(
            "scope matching is ordinal, so an upper-cased atom is a different string");
    }

    [Fact]
    public void ResolvedScopes_AreMatchedOrdinally_OnTheScopedCredentialPath()
    {
        var resolved = Resolve(
            [Scope.GlucoseReadWrite], AuthType.OAuthAccessToken, Scope.GlucoseRead);

        resolved.Should().Contain(Scope.GlucoseRead);
        resolved.Contains(Scope.GlucoseRead.ToUpperInvariant()).Should().BeFalse(
            "scope matching is ordinal, so an upper-cased atom is a different string");
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.DirectGrant)]
    public void Superuser_OnScopedCredential_KeepsOnlyTheCredentialScopes(AuthType authType)
    {
        // An owner who authorized a third-party app for glucose.read must not hand it superuser.
        var resolved = Resolve([Scope.FullAccess], authType, Scope.GlucoseRead);

        resolved.Should().BeEquivalentTo([Scope.GlucoseRead]);
        resolved.Should().NotContain(Scope.FullAccess);
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.DirectGrant)]
    public void Superuser_OnFullAccessCredential_KeepsTheWildcard(AuthType authType)
    {
        // "*" on the credential bounds nothing, so the owner keeps superuser.
        Resolve([Scope.FullAccess], authType, Scope.FullAccess)
            .Should().Contain(Scope.FullAccess);
    }

    // ---- the readwrite/read downgrade ----

    [Fact]
    public void ReadWriteMembership_OnReadOnlyCredential_DowngradesToRead()
    {
        // SatisfiesScope answers false for a readwrite requirement met only by read, and
        // NormalizeMemberPermissions adds no read counterpart, so this member previously resolved
        // to NEITHER scope. Both sides permit the read counterpart.
        var resolved = Resolve(
            [Scope.GlucoseReadWrite], AuthType.OAuthAccessToken, Scope.GlucoseRead);

        resolved.Should().BeEquivalentTo([Scope.GlucoseRead]);
        resolved.Should().NotContain(Scope.GlucoseReadWrite);
    }

    [Fact]
    public void ReadWriteMembership_OnReadOnlyCredential_DowngradesEveryTieredScope()
    {
        var readWriteRole = RoleSeeds.Permissions[RoleSeeds.Admin];
        var readOnlyCredential = new[]
        {
            Scope.GlucoseRead, Scope.TreatmentsRead, Scope.TherapyRead,
            Scope.AlertsRead, Scope.FoodRead, Scope.DevicesRead,
        };

        var resolved = Resolve(readWriteRole, AuthType.OAuthAccessToken, readOnlyCredential);

        resolved.Should().BeEquivalentTo(readOnlyCredential);
    }

    [Fact]
    public void ReadOnlyMembership_OnReadWriteCredential_StaysRead()
    {
        // The downgrade never runs backwards: membership is still the other bound.
        Resolve([Scope.GlucoseRead], AuthType.OAuthAccessToken, Scope.GlucoseReadWrite)
            .Should().BeEquivalentTo([Scope.GlucoseRead]);
    }

    // ---- administration atoms ----

    [Fact]
    public void AdministrationAtoms_SurviveOnAnUnscopedCredential()
    {
        var resolved = Resolve(
            [Scope.MembersManage, Scope.AuditRead], AuthType.SessionCookie);

        resolved.Should().Contain(Scope.MembersManage);
        resolved.Should().Contain(Scope.AuditRead);
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.DirectGrant)]
    public void AdministrationAtoms_NeverSurviveOnAScopedCredential(AuthType authType)
    {
        // A delegated credential never administers the tenant, even when it presents an
        // administration atom outright. Non-requestability at /authorize means no legitimate
        // credential holds one; the resolver bounds the credential's scopes by the request
        // vocabulary so an atom that arrived by any other route is dropped here too.
        var resolved = Resolve(
            RoleSeeds.Permissions[RoleSeeds.Admin],
            authType,
            Scope.GlucoseRead,
            Scope.MembersManage, Scope.RolesManage,
            Scope.TenantSettings, Scope.AuditRead,
            Scope.SharingManage, Scope.SharingGuest);

        resolved.Should().BeEquivalentTo([Scope.GlucoseRead]);

        foreach (var atom in new[]
                 {
                     Scope.MembersManage, Scope.RolesManage,
                     Scope.TenantSettings, Scope.AuditRead,
                     Scope.SharingManage, Scope.SharingGuest,
                 })
        {
            Scope.Satisfies(resolved, atom).Should().BeFalse(
                $"a delegated credential must not administer the tenant via '{atom}'");
        }
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.DirectGrant)]
    public void ScopedCredential_CannotWidenItselfWithANonRequestableScope(AuthType authType)
    {
        // The credential presents only administration atoms. It resolves to nothing rather than to
        // tenant administration, so a forged or hand-written grant row cannot escalate.
        Resolve(
            RoleSeeds.Permissions[RoleSeeds.Admin],
            authType,
            Scope.MembersManage, Scope.RolesManage)
            .Should().BeEmpty();
    }

    // ---- member-personal device capabilities ----

    [Fact]
    public void StaleRoleWithoutDeviceAtoms_StillResolvesDeviceScopes_OnUnscopedCredential()
    {
        // Seed role rows are never reconciled, so a tenant seeded before these atoms existed has
        // role rows without them. They authorize the member's OWN client devices, not patient data.
        var resolved = Resolve([Scope.GlucoseRead], AuthType.SessionCookie);

        resolved.Should().Contain(Scope.DeviceNotify);
        resolved.Should().Contain(Scope.DeviceActuate);
    }

    [Fact]
    public void StaleRoleWithoutDeviceAtoms_StillResolvesDeviceScopes_FromAScopedCredential()
    {
        var resolved = Resolve(
            [Scope.GlucoseRead], AuthType.OAuthAccessToken,
            Scope.GlucoseRead, Scope.DeviceNotify);

        resolved.Should().Contain(Scope.DeviceNotify);
        // The credential did not carry device.actuate, so membership must not add it.
        resolved.Should().NotContain(Scope.DeviceActuate);
    }

    [Fact]
    public void ZeroPermissionMember_GetsNoDeviceScopes_FromAScopedCredential()
    {
        Resolve([], AuthType.OAuthAccessToken, Scope.DeviceNotify, Scope.DeviceActuate)
            .Should().BeEmpty();
    }

    // ---- unknown atoms ----

    [Fact]
    public void UnknownPermissionAtoms_AreDropped()
    {
        // Role rows written before the 2026-05-12 atom rename hold strings the vocabulary no longer
        // recognizes (entries.* -> glucose.*, profile.* -> therapy.*). They resolve to nothing
        // rather than leaking through as opaque scope strings.
        var resolved = Resolve(
            ["entries.read", "profile.read", "members.destroy", Scope.GlucoseRead],
            AuthType.SessionCookie);

        resolved.Should().NotContain("entries.read");
        resolved.Should().NotContain("profile.read");
        resolved.Should().NotContain("members.destroy");
        resolved.Should().Contain(Scope.GlucoseRead);
    }

    [Fact]
    public void Resolve_DoesNotMutateTheCallersPermissionSet()
    {
        var permissions = new HashSet<string> { Scope.GlucoseRead };

        MemberScopeResolver.Resolve(permissions, AuthType.SessionCookie, NoScopes);

        permissions.Should().BeEquivalentTo([Scope.GlucoseRead]);
    }
}
