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
        Resolve([TenantPermissions.Superuser], authType).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(UnscopedTypes))]
    public void UnscopedCredential_WithNoScopes_ResolvesFromMembership(AuthType authType)
    {
        Resolve([TenantPermissions.GlucoseRead], authType)
            .Should().Contain(OAuthScopes.GlucoseRead);
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
            TenantPermissions.SeedRolePermissions[roleSlug], authType);

        foreach (var scope in expected)
            TenantPermissions.HasPermission(resolved, scope).Should().BeTrue($"'{scope}' is granted");
        foreach (var scope in notExpected)
            TenantPermissions.HasPermission(resolved, scope).Should().BeFalse($"'{scope}' is not granted");
    }

    /// <summary>
    /// Every seed role crossed with every unscoped credential type: the permissions the role must
    /// resolve to, and the permissions it must not reach. Asserted through
    /// <see cref="TenantPermissions.HasPermission"/> because that is the gate the administration and
    /// data controllers actually apply to <c>GrantedScopes</c>.
    /// </summary>
    public static TheoryData<string, AuthType, string[], string[]> SeedRoleExpectations()
    {
        (string Role, string[] Expected, string[] NotExpected)[] roles =
        [
            (
                TenantPermissions.SeedRoles.Owner,
                // "*" satisfies every atom through TenantPermissions.Satisfies.
                [TenantPermissions.Superuser, TenantPermissions.GlucoseReadWrite, TenantPermissions.AuditManage],
                []
            ),
            (
                TenantPermissions.SeedRoles.Admin,
                [
                    TenantPermissions.GlucoseReadWrite, TenantPermissions.TreatmentsReadWrite,
                    TenantPermissions.TherapyReadWrite, TenantPermissions.FoodReadWrite,
                    TenantPermissions.AlertsReadWrite, TenantPermissions.DevicesReadWrite,
                    TenantPermissions.SleepReadWrite, TenantPermissions.ReportsRead,
                    TenantPermissions.MembersManage, TenantPermissions.MembersInvite,
                    TenantPermissions.TenantSettings, TenantPermissions.RolesManage,
                    TenantPermissions.SharingManage, TenantPermissions.SharingGuest,
                    TenantPermissions.AuditRead,
                    TenantPermissions.DeviceNotify, TenantPermissions.DeviceActuate,
                ],
                // audit.manage is Owner-only by design, and an Administrator is not a superuser.
                [TenantPermissions.Superuser, TenantPermissions.AuditManage]
            ),
            (
                TenantPermissions.SeedRoles.Clinician,
                [
                    TenantPermissions.GlucoseRead, TenantPermissions.TreatmentsRead,
                    TenantPermissions.TherapyRead, TenantPermissions.AlertsRead,
                    TenantPermissions.ReportsRead, TenantPermissions.SleepRead,
                ],
                [
                    TenantPermissions.Superuser, TenantPermissions.GlucoseReadWrite,
                    TenantPermissions.TreatmentsReadWrite, TenantPermissions.TherapyReadWrite,
                    TenantPermissions.AlertsReadWrite,
                    TenantPermissions.MembersManage, TenantPermissions.TenantSettings,
                    TenantPermissions.AuditRead,
                ]
            ),
            (
                TenantPermissions.SeedRoles.Caretaker,
                [
                    TenantPermissions.GlucoseRead, TenantPermissions.TreatmentsReadWrite,
                    TenantPermissions.TherapyRead, TenantPermissions.AlertsReadWrite,
                    TenantPermissions.FoodRead,
                ],
                [
                    TenantPermissions.Superuser, TenantPermissions.GlucoseReadWrite,
                    TenantPermissions.TherapyReadWrite, TenantPermissions.FoodReadWrite,
                    TenantPermissions.MembersManage, TenantPermissions.TenantSettings,
                    TenantPermissions.AuditRead,
                ]
            ),
            (
                TenantPermissions.SeedRoles.Viewer,
                [TenantPermissions.GlucoseRead, TenantPermissions.ReportsRead],
                [
                    TenantPermissions.Superuser, TenantPermissions.GlucoseReadWrite,
                    TenantPermissions.TreatmentsRead, TenantPermissions.TherapyRead,
                    TenantPermissions.TenantSettings, TenantPermissions.MembersManage,
                ]
            ),
            (
                TenantPermissions.SeedRoles.Denied,
                [],
                [
                    TenantPermissions.Superuser, TenantPermissions.GlucoseRead,
                    TenantPermissions.ReportsRead, TenantPermissions.DeviceNotify,
                    TenantPermissions.DeviceActuate, TenantPermissions.MembersManage,
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
        Resolve(TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Denied],
            AuthType.SessionCookie).Should().BeEmpty();
    }

    // ---- superuser ----

    [Fact]
    public void Superuser_OnUnscopedCredential_KeepsTheWildcard()
    {
        Resolve([TenantPermissions.Superuser], AuthType.SessionCookie)
            .Should().Contain(TenantPermissions.Superuser);
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.DirectGrant)]
    public void Superuser_OnScopedCredential_KeepsOnlyTheCredentialScopes(AuthType authType)
    {
        // An owner who authorized a third-party app for glucose.read must not hand it superuser.
        var resolved = Resolve([TenantPermissions.Superuser], authType, OAuthScopes.GlucoseRead);

        resolved.Should().BeEquivalentTo([OAuthScopes.GlucoseRead]);
        resolved.Should().NotContain(TenantPermissions.Superuser);
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.DirectGrant)]
    public void Superuser_OnFullAccessCredential_KeepsTheWildcard(AuthType authType)
    {
        // "*" on the credential bounds nothing, so the owner keeps superuser.
        Resolve([TenantPermissions.Superuser], authType, OAuthScopes.FullAccess)
            .Should().Contain(OAuthScopes.FullAccess);
    }

    // ---- the readwrite/read downgrade ----

    [Fact]
    public void ReadWriteMembership_OnReadOnlyCredential_DowngradesToRead()
    {
        // SatisfiesScope answers false for a readwrite requirement met only by read, and
        // NormalizeMemberPermissions adds no read counterpart, so this member previously resolved
        // to NEITHER scope. Both sides permit the read counterpart.
        var resolved = Resolve(
            [TenantPermissions.GlucoseReadWrite], AuthType.OAuthAccessToken, OAuthScopes.GlucoseRead);

        resolved.Should().BeEquivalentTo([OAuthScopes.GlucoseRead]);
        resolved.Should().NotContain(OAuthScopes.GlucoseReadWrite);
    }

    [Fact]
    public void ReadWriteMembership_OnReadOnlyCredential_DowngradesEveryTieredScope()
    {
        var readWriteRole = TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Admin];
        var readOnlyCredential = new[]
        {
            OAuthScopes.GlucoseRead, OAuthScopes.TreatmentsRead, OAuthScopes.TherapyRead,
            OAuthScopes.AlertsRead, OAuthScopes.FoodRead, OAuthScopes.DevicesRead,
        };

        var resolved = Resolve(readWriteRole, AuthType.OAuthAccessToken, readOnlyCredential);

        resolved.Should().BeEquivalentTo(readOnlyCredential);
    }

    [Fact]
    public void ReadOnlyMembership_OnReadWriteCredential_StaysRead()
    {
        // The downgrade never runs backwards: membership is still the other bound.
        Resolve([TenantPermissions.GlucoseRead], AuthType.OAuthAccessToken, OAuthScopes.GlucoseReadWrite)
            .Should().BeEquivalentTo([OAuthScopes.GlucoseRead]);
    }

    // ---- administration atoms ----

    [Fact]
    public void AdministrationAtoms_SurviveOnAnUnscopedCredential()
    {
        var resolved = Resolve(
            [TenantPermissions.MembersManage, TenantPermissions.AuditRead], AuthType.SessionCookie);

        resolved.Should().Contain(TenantPermissions.MembersManage);
        resolved.Should().Contain(TenantPermissions.AuditRead);
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
            TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Admin],
            authType,
            OAuthScopes.GlucoseRead,
            TenantPermissions.MembersManage, TenantPermissions.RolesManage,
            TenantPermissions.TenantSettings, TenantPermissions.AuditRead,
            TenantPermissions.SharingManage, TenantPermissions.SharingGuest);

        resolved.Should().BeEquivalentTo([OAuthScopes.GlucoseRead]);

        foreach (var atom in new[]
                 {
                     TenantPermissions.MembersManage, TenantPermissions.RolesManage,
                     TenantPermissions.TenantSettings, TenantPermissions.AuditRead,
                     TenantPermissions.SharingManage, TenantPermissions.SharingGuest,
                 })
        {
            TenantPermissions.HasPermission(resolved, atom).Should().BeFalse(
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
            TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Admin],
            authType,
            TenantPermissions.MembersManage, TenantPermissions.RolesManage)
            .Should().BeEmpty();
    }

    // ---- member-personal device capabilities ----

    [Fact]
    public void StaleRoleWithoutDeviceAtoms_StillResolvesDeviceScopes_OnUnscopedCredential()
    {
        // Seed role rows are never reconciled, so a tenant seeded before these atoms existed has
        // role rows without them. They authorize the member's OWN client devices, not patient data.
        var resolved = Resolve([TenantPermissions.GlucoseRead], AuthType.SessionCookie);

        resolved.Should().Contain(OAuthScopes.DeviceNotify);
        resolved.Should().Contain(OAuthScopes.DeviceActuate);
    }

    [Fact]
    public void StaleRoleWithoutDeviceAtoms_StillResolvesDeviceScopes_FromAScopedCredential()
    {
        var resolved = Resolve(
            [TenantPermissions.GlucoseRead], AuthType.OAuthAccessToken,
            OAuthScopes.GlucoseRead, OAuthScopes.DeviceNotify);

        resolved.Should().Contain(OAuthScopes.DeviceNotify);
        // The credential did not carry device.actuate, so membership must not add it.
        resolved.Should().NotContain(OAuthScopes.DeviceActuate);
    }

    [Fact]
    public void ZeroPermissionMember_GetsNoDeviceScopes_FromAScopedCredential()
    {
        Resolve([], AuthType.OAuthAccessToken, OAuthScopes.DeviceNotify, OAuthScopes.DeviceActuate)
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
            ["entries.read", "profile.read", "members.destroy", TenantPermissions.GlucoseRead],
            AuthType.SessionCookie);

        resolved.Should().NotContain("entries.read");
        resolved.Should().NotContain("profile.read");
        resolved.Should().NotContain("members.destroy");
        resolved.Should().Contain(OAuthScopes.GlucoseRead);
    }

    [Fact]
    public void Resolve_DoesNotMutateTheCallersPermissionSet()
    {
        var permissions = new HashSet<string> { TenantPermissions.GlucoseRead };

        MemberScopeResolver.Resolve(permissions, AuthType.SessionCookie, NoScopes);

        permissions.Should().BeEquivalentTo([TenantPermissions.GlucoseRead]);
    }
}
