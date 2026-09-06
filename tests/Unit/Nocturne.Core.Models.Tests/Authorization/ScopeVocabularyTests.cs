using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// Pins the relationship between the tenant RBAC permission vocabulary
/// (<see cref="Scope"/>) and the OAuth scope vocabulary
/// (<see cref="Scope"/>). <c>MemberScopeMiddleware</c> converts a member's effective
/// permissions into granted scopes, so an atom the scope vocabulary does not recognize is
/// silently deleted and the permission becomes unenforceable.
/// </summary>
[Trait("Category", "Unit")]
public class ScopeVocabularyTests
{
    /// <summary>The tenant-administration atoms: grantable through a tenant role, never requestable.</summary>
    private static readonly string[] AdministrationAtoms =
    [
        Scope.MembersInvite,
        Scope.MembersManage,
        Scope.RolesManage,
        Scope.TenantSettings,
        Scope.SharingManage,
        Scope.SharingGuest,
        Scope.AuditRead,
        Scope.AuditManage,
    ];

    [Fact]
    public void EveryTenantPermissionAtom_IsGrantableAsAScope()
    {
        Scope.MemberGrantableScopes.Should().Contain(Scope.PermissionAtoms);
    }

    [Fact]
    public void NormalizeMemberPermissions_PreservesEveryTenantPermissionAtom()
    {
        Scope.NormalizeMemberPermissions(Scope.PermissionAtoms)
            .Should().Contain(Scope.PermissionAtoms);
    }

    [Fact]
    public void NormalizeMemberPermissions_PreservesAdministrationAtoms()
    {
        Scope.NormalizeMemberPermissions(AdministrationAtoms)
            .Should().BeEquivalentTo(AdministrationAtoms);
    }

    [Fact]
    public void NormalizeMemberPermissions_StillDropsUnknownAtoms()
    {
        Scope.NormalizeMemberPermissions(new[] { "glucose.read", "members.destroy", "entries.readwrite" })
            .Should().BeEquivalentTo([Scope.GlucoseRead]);
    }

    [Fact]
    public void AdministrationAtoms_AreNotRequestableByAClient()
    {
        // /authorize, the direct-grant validator and dynamic client registration all gate on
        // ValidRequestScopes. There is no per-client scope ceiling, so a requestable
        // administration atom would let any client ask a user to consent to tenant administration.
        foreach (var atom in AdministrationAtoms)
        {
            Scope.IsValid(atom).Should().BeFalse($"'{atom}' must not be requestable");
            Scope.ValidRequestScopes.Should().NotContain(atom);
        }
    }

    [Fact]
    public void Normalize_DropsAdministrationAtoms()
    {
        Scope.Normalize(AdministrationAtoms).Should().BeEmpty();
    }

    [Fact]
    public void FullAccessExpansion_DoesNotEnumerateAdministrationAtoms()
    {
        // A "*" grant keeps satisfying every permission through the FullAccess shortcut in
        // SatisfiesScope/Scope.Satisfies; what must not change is the literal
        // expansion, which is what share narrowing and the scope-display surfaces enumerate.
        var expanded = Scope.Normalize([Scope.FullAccess]);

        expanded.Should().Contain(Scope.FullAccess);
        expanded.Should().NotIntersectWith(AdministrationAtoms);
        Scope.AllScopes.Should().NotIntersectWith(AdministrationAtoms);

        foreach (var atom in AdministrationAtoms)
        {
            Scope.Satisfies(expanded, atom).Should().BeTrue();
            Scope.Satisfies(expanded, atom).Should().BeTrue();
        }
    }

    [Fact]
    public void PublicShareScopes_ContainNoAdministrationAtom()
    {
        Scope.PublicShareScopes.Should().NotIntersectWith(AdministrationAtoms);
        Scope.PublicShareScopes.Should().OnlyContain(scope => scope.EndsWith(".read"));
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
        // Derived, not hand-listed: a new atom in Scope.PermissionAtoms is grantable with no second
        // edit, so the two vocabularies cannot drift.
        Scope.MemberGrantableScopes.Should().BeEquivalentTo(
            Scope.ValidRequestScopes.Concat(Scope.PermissionAtoms).ToHashSet());
    }

    [Theory]
    [InlineData(Scope.GlucoseReadWrite, Scope.GlucoseRead)]
    [InlineData(Scope.TreatmentsReadWrite, Scope.TreatmentsRead)]
    [InlineData(Scope.DevicesReadWrite, Scope.DevicesRead)]
    [InlineData(Scope.TherapyReadWrite, Scope.TherapyRead)]
    [InlineData(Scope.AlertsReadWrite, Scope.AlertsRead)]
    [InlineData(Scope.HeartRateReadWrite, Scope.HeartRateRead)]
    [InlineData(Scope.StepCountReadWrite, Scope.StepCountRead)]
    [InlineData(Scope.SleepReadWrite, Scope.SleepRead)]
    [InlineData(Scope.FoodReadWrite, Scope.FoodRead)]
    public void TryGetImpliedReadScope_NarrowsEveryTieredScope(string readWrite, string expectedRead)
    {
        Scope.TryGetImpliedReadScope(readWrite, out var readScope).Should().BeTrue();
        readScope.Should().Be(expectedRead);
    }

    [Theory]
    [InlineData(Scope.GlucoseRead)]
    [InlineData(Scope.ReportsRead)]
    [InlineData(Scope.IdentityRead)]
    [InlineData(Scope.DeviceNotify)]
    [InlineData(Scope.DeviceActuate)]
    [InlineData(Scope.FullAccess)]
    [InlineData(Scope.MembersManage)]
    public void TryGetImpliedReadScope_HasNoCounterpartForAnUntieredScope(string scope)
    {
        // Notably "*": a superuser membership on a read-only credential must not downgrade the
        // wildcard to some read scope, it must simply not survive.
        Scope.TryGetImpliedReadScope(scope, out _).Should().BeFalse();
    }

    [Fact]
    public void SatisfiesScope_DoesNotTreatReadAsSatisfyingReadWrite()
    {
        // The asymmetry the downgrade in MemberScopeResolver exists to handle. Pinned here so the
        // downgrade is not silently made redundant (or contradicted) by a change to SatisfiesScope.
        Scope.Satisfies([Scope.GlucoseRead], Scope.GlucoseReadWrite)
            .Should().BeFalse();
        Scope.Satisfies([Scope.GlucoseReadWrite], Scope.GlucoseRead)
            .Should().BeTrue();
    }

    [Fact]
    public void NormalizeMemberPermissions_DoesNotAddTheReadCounterpartOfAReadWriteScope()
    {
        // The other half of the asymmetry: normalization leaves readwrite alone, so an intersection
        // against a read-only credential had nothing to match.
        Scope.NormalizeMemberPermissions([Scope.GlucoseReadWrite])
            .Should().BeEquivalentTo([Scope.GlucoseReadWrite]);
    }
}
