using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// Pins the relationship between the tenant RBAC permission vocabulary
/// (<see cref="TenantPermissions"/>) and the OAuth scope vocabulary
/// (<see cref="OAuthScopes"/>). <c>MemberScopeMiddleware</c> converts a member's effective
/// permissions into granted scopes, so an atom the scope vocabulary does not recognize is
/// silently deleted and the permission becomes unenforceable.
/// </summary>
[Trait("Category", "Unit")]
public class OAuthScopeVocabularyTests
{
    /// <summary>The tenant-administration atoms: grantable through a tenant role, never requestable.</summary>
    private static readonly string[] AdministrationAtoms =
    [
        TenantPermissions.MembersInvite,
        TenantPermissions.MembersManage,
        TenantPermissions.RolesManage,
        TenantPermissions.TenantSettings,
        TenantPermissions.SharingManage,
        TenantPermissions.SharingGuest,
        TenantPermissions.AuditRead,
        TenantPermissions.AuditManage,
    ];

    [Fact]
    public void EveryTenantPermissionAtom_IsGrantableAsAScope()
    {
        OAuthScopes.MemberGrantableScopes.Should().Contain(TenantPermissions.All);
    }

    [Fact]
    public void NormalizeMemberPermissions_PreservesEveryTenantPermissionAtom()
    {
        OAuthScopes.NormalizeMemberPermissions(TenantPermissions.All)
            .Should().Contain(TenantPermissions.All);
    }

    [Fact]
    public void NormalizeMemberPermissions_PreservesAdministrationAtoms()
    {
        OAuthScopes.NormalizeMemberPermissions(AdministrationAtoms)
            .Should().BeEquivalentTo(AdministrationAtoms);
    }

    [Fact]
    public void NormalizeMemberPermissions_StillDropsUnknownAtoms()
    {
        OAuthScopes.NormalizeMemberPermissions(new[] { "glucose.read", "members.destroy", "entries.readwrite" })
            .Should().BeEquivalentTo([OAuthScopes.GlucoseRead]);
    }

    [Fact]
    public void AdministrationAtoms_AreNotRequestableByAClient()
    {
        // /authorize, the direct-grant validator and dynamic client registration all gate on
        // ValidRequestScopes. There is no per-client scope ceiling, so a requestable
        // administration atom would let any client ask a user to consent to tenant administration.
        foreach (var atom in AdministrationAtoms)
        {
            OAuthScopes.IsValid(atom).Should().BeFalse($"'{atom}' must not be requestable");
            OAuthScopes.ValidRequestScopes.Should().NotContain(atom);
        }
    }

    [Fact]
    public void Normalize_DropsAdministrationAtoms()
    {
        OAuthScopes.Normalize(AdministrationAtoms).Should().BeEmpty();
    }

    [Fact]
    public void FullAccessExpansion_DoesNotEnumerateAdministrationAtoms()
    {
        // A "*" grant keeps satisfying every permission through the FullAccess shortcut in
        // SatisfiesScope/TenantPermissions.Satisfies; what must not change is the literal
        // expansion, which is what share narrowing and the scope-display surfaces enumerate.
        var expanded = OAuthScopes.Normalize([OAuthScopes.FullAccess]);

        expanded.Should().Contain(OAuthScopes.FullAccess);
        expanded.Should().NotIntersectWith(AdministrationAtoms);
        OAuthScopes.AllScopes.Should().NotIntersectWith(AdministrationAtoms);

        foreach (var atom in AdministrationAtoms)
        {
            OAuthScopes.SatisfiesScope(expanded, atom).Should().BeTrue();
            TenantPermissions.HasPermission(expanded, atom).Should().BeTrue();
        }
    }

    [Fact]
    public void PublicShareScopes_ContainNoAdministrationAtom()
    {
        TenantPermissions.PublicShareScopes.Should().NotIntersectWith(AdministrationAtoms);
        TenantPermissions.PublicShareScopes.Should().OnlyContain(scope => scope.EndsWith(".read"));
    }

    [Fact]
    public void ScopeTranslator_MapsNoLegacyPermissionToAnAdministrationAtom()
    {
        // The legacy Shiro trie has no tenant-administration equivalent. A mapping would let a
        // migrated Nightscout api-secret carrying api:*:update administer the tenant.
        string[] legacyPermissions =
        [
            "api:*:read", "api:*:create", "api:*:update", "api:*:delete",
            "api:entries:read", "api:entries:create", "api:treatments:update",
            "api:devicestatus:create", "api:food:update", "api:profile:create",
            "api:entries:*", "api:treatments:*", "api:devicestatus:*", "api:food:*",
            "api:profile:*", "api:activity:*", "*:*:read",
            "readable",
        ];

        ScopeTranslator.FromPermissions(legacyPermissions).Should().NotIntersectWith(AdministrationAtoms);
    }

    [Fact]
    public void ScopeTranslator_TranslatesNoAdministrationAtomToATriePermission()
    {
        // The rebuilt PermissionTrie drives the legacy HasPermissions policy. Administration atoms
        // are enforced against GrantedScopes only, so they must not reach the trie.
        ScopeTranslator.ToPermissions(AdministrationAtoms).Should().BeEmpty();
    }

    [Fact]
    public void MemberGrantableScopes_IsDerivedFromTheTenantPermissionVocabulary()
    {
        // Derived, not hand-listed: a new atom in TenantPermissions.All is grantable with no second
        // edit, so the two vocabularies cannot drift.
        OAuthScopes.MemberGrantableScopes.Should().BeEquivalentTo(
            OAuthScopes.ValidRequestScopes.Concat(TenantPermissions.All).ToHashSet());
    }

    [Theory]
    [InlineData(OAuthScopes.GlucoseReadWrite, OAuthScopes.GlucoseRead)]
    [InlineData(OAuthScopes.TreatmentsReadWrite, OAuthScopes.TreatmentsRead)]
    [InlineData(OAuthScopes.DevicesReadWrite, OAuthScopes.DevicesRead)]
    [InlineData(OAuthScopes.TherapyReadWrite, OAuthScopes.TherapyRead)]
    [InlineData(OAuthScopes.AlertsReadWrite, OAuthScopes.AlertsRead)]
    [InlineData(OAuthScopes.HeartRateReadWrite, OAuthScopes.HeartRateRead)]
    [InlineData(OAuthScopes.StepCountReadWrite, OAuthScopes.StepCountRead)]
    [InlineData(OAuthScopes.SleepReadWrite, OAuthScopes.SleepRead)]
    [InlineData(OAuthScopes.FoodReadWrite, OAuthScopes.FoodRead)]
    public void TryGetImpliedReadScope_NarrowsEveryTieredScope(string readWrite, string expectedRead)
    {
        OAuthScopes.TryGetImpliedReadScope(readWrite, out var readScope).Should().BeTrue();
        readScope.Should().Be(expectedRead);
    }

    [Theory]
    [InlineData(OAuthScopes.GlucoseRead)]
    [InlineData(OAuthScopes.ReportsRead)]
    [InlineData(OAuthScopes.IdentityRead)]
    [InlineData(OAuthScopes.DeviceNotify)]
    [InlineData(OAuthScopes.DeviceActuate)]
    [InlineData(OAuthScopes.FullAccess)]
    [InlineData(TenantPermissions.MembersManage)]
    public void TryGetImpliedReadScope_HasNoCounterpartForAnUntieredScope(string scope)
    {
        // Notably "*": a superuser membership on a read-only credential must not downgrade the
        // wildcard to some read scope, it must simply not survive.
        OAuthScopes.TryGetImpliedReadScope(scope, out _).Should().BeFalse();
    }

    [Fact]
    public void SatisfiesScope_DoesNotTreatReadAsSatisfyingReadWrite()
    {
        // The asymmetry the downgrade in MemberScopeResolver exists to handle. Pinned here so the
        // downgrade is not silently made redundant (or contradicted) by a change to SatisfiesScope.
        OAuthScopes.SatisfiesScope([OAuthScopes.GlucoseRead], OAuthScopes.GlucoseReadWrite)
            .Should().BeFalse();
        OAuthScopes.SatisfiesScope([OAuthScopes.GlucoseReadWrite], OAuthScopes.GlucoseRead)
            .Should().BeTrue();
    }

    [Fact]
    public void NormalizeMemberPermissions_DoesNotAddTheReadCounterpartOfAReadWriteScope()
    {
        // The other half of the asymmetry: normalization leaves readwrite alone, so an intersection
        // against a read-only credential had nothing to match.
        OAuthScopes.NormalizeMemberPermissions([TenantPermissions.GlucoseReadWrite])
            .Should().BeEquivalentTo([OAuthScopes.GlucoseReadWrite]);
    }
}
