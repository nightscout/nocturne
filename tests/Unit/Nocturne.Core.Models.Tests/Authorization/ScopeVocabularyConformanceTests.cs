using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// Structural invariants of <see cref="Scope"/> and <see cref="RoleSeeds"/>. They used to be held by
/// hand across two duplicated declarations of the same vocabulary; with one declaration left, the
/// remaining failure mode is a subset drifting out of step with the atoms it draws from — a rename
/// that leaves a stale string behind in a curated set, where nothing but a runtime denial would
/// notice.
/// </summary>
[Trait("Category", "Unit")]
public class ScopeVocabularyConformanceTests
{
    /// <summary>
    /// Every string the vocabulary recognises anywhere: an atom a role may confer, plus the aliases
    /// and full access a client may request.
    /// </summary>
    private static readonly IReadOnlySet<string> KnownAtoms =
        new HashSet<string>(Scope.PermissionAtoms.Concat(Scope.ValidRequestScopes), StringComparer.Ordinal);

    private static void AllShouldBeKnownAtoms(IEnumerable<string> scopes, string setName)
    {
        var listed = scopes.ToList();

        listed.Should().NotBeEmpty($"'{setName}' should list at least one scope, or this check proves nothing");

        foreach (var scope in listed)
        {
            KnownAtoms.Should().Contain(scope,
                $"'{scope}' in '{setName}' should be a scope the vocabulary still declares");
        }
    }

    // ---- the sets ----

    [Fact]
    public void MemberGrantableScopes_IsAStrictSupersetOfPermissionAtoms()
    {
        // Scope.ValidateGrant checks a granter's SCOPE set against a requested PERMISSION atom, so
        // it is only sound while every permission atom survives scope resolution.
        Scope.PermissionAtoms.Should().NotBeEmpty("an empty vocabulary would make this check vacuous");

        Scope.MemberGrantableScopes.Should().Contain(Scope.PermissionAtoms,
            "every permission atom should also be grantable as a scope");
        Scope.MemberGrantableScopes.Except(Scope.PermissionAtoms).Should().NotBeEmpty(
            "the superset should be strict: full access and the aliases are grantable but are not permission atoms");
    }

    [Fact]
    public void AllScopes_HasNoDuplicates()
    {
        Scope.AllScopes.Should().NotBeEmpty("an empty vocabulary would make this check vacuous");
        Scope.AllScopes.Should().OnlyHaveUniqueItems(
            "a duplicated entry would inflate every count derived from the list");
    }

    [Fact]
    public void AllScopes_IsEntirelyRequestable()
    {
        Scope.AllScopes.Should().NotBeEmpty("an empty vocabulary would make this check vacuous");

        foreach (var scope in Scope.AllScopes)
        {
            Scope.IsValid(scope).Should().BeTrue(
                $"'{scope}' is in AllScopes, so a client should be able to request it at /authorize");
        }
    }

    [Fact]
    public void ValidRequestScopes_IsAllScopesPlusTheAliasesAndFullAccess()
    {
        // The two sets are the OAuth surface: AllScopes is what a grant may hold, ValidRequestScopes
        // is what may be asked for. They differ by exactly the three entries normalization expands.
        Scope.ValidRequestScopes.Should().BeEquivalentTo(
            Scope.AllScopes.Concat([Scope.FullAccess, Scope.HealthRead, Scope.HealthReadWrite]),
            "the requestable set should be AllScopes widened by full access and the two health aliases, and nothing else");
    }

    [Fact]
    public void PublicShareScopes_AreKnownAtoms()
    {
        AllShouldBeKnownAtoms(Scope.PublicShareScopes, nameof(Scope.PublicShareScopes));
    }

    [Fact]
    public void DefaultPublicShareScopes_AreKnownAtoms()
    {
        AllShouldBeKnownAtoms(Scope.DefaultPublicShareScopes, nameof(Scope.DefaultPublicShareScopes));
    }

    [Fact]
    public void AllowedGuestScopes_AreKnownAtoms()
    {
        AllShouldBeKnownAtoms(Scope.AllowedGuestScopes, nameof(Scope.AllowedGuestScopes));
    }

    [Fact]
    public void MemberPersonalScopes_AreKnownAtoms()
    {
        AllShouldBeKnownAtoms(Scope.MemberPersonalScopes, nameof(Scope.MemberPersonalScopes));
    }

    [Fact]
    public void DemoVisitorPermissions_AreKnownAtoms()
    {
        AllShouldBeKnownAtoms(Scope.DemoVisitorPermissions, nameof(Scope.DemoVisitorPermissions));
    }

    // ---- the seed roles ----

    [Fact]
    public void RoleSeeds_ConferOnlyKnownAtoms()
    {
        RoleSeeds.Permissions.Should().NotBeEmpty("an empty seed table would make this check vacuous");
        RoleSeeds.Permissions.SelectMany(entry => entry.Value).Should().NotBeEmpty(
            "the seed roles should confer at least one scope between them");

        foreach (var (role, permissions) in RoleSeeds.Permissions)
        {
            foreach (var permission in permissions)
            {
                KnownAtoms.Should().Contain(permission,
                    $"the '{role}' seed role confers '{permission}', which should still be in the vocabulary");
            }
        }
    }

    [Fact]
    public void RoleSeeds_DisplayNamesCoverExactlyTheRolesThatHavePermissions()
    {
        RoleSeeds.Permissions.Should().NotBeEmpty("an empty seed table would make this check vacuous");

        RoleSeeds.DisplayNames.Keys.Should().BeEquivalentTo(RoleSeeds.Permissions.Keys,
            "every seeded role should have a display name and every display name should belong to a seeded role");
    }

    // ---- the alias expansions ----

    [Fact]
    public void HealthReadExpansion_ContainsOnlyKnownAtoms()
    {
        AllShouldBeKnownAtoms(Scope.HealthReadExpansion, nameof(Scope.HealthReadExpansion));
    }

    [Fact]
    public void HealthReadWriteExpansion_ContainsOnlyKnownAtoms()
    {
        AllShouldBeKnownAtoms(Scope.HealthReadWriteExpansion, nameof(Scope.HealthReadWriteExpansion));
    }

    [Fact]
    public void HealthExpansions_CoverTheSameNumberOfCategories()
    {
        // The two aliases are the same categories at two tiers, so a category added to one and
        // forgotten in the other is the drift to catch.
        Scope.HealthReadExpansion.Should().NotBeEmpty("an empty expansion would make this check vacuous");
        Scope.HealthReadExpansion.Should().HaveSameCount(Scope.HealthReadWriteExpansion,
            "health.read and health.readwrite should expand over the same data categories");
    }

    /// <summary>
    /// The security-relevant half of the two-set split: a tenant role may confer the administration
    /// atoms, but an OAuth client must not be able to ask for them at <c>/authorize</c>. There is no
    /// per-client ceiling that could bound such a request and no consent screen that could describe
    /// it, so a client that could request <c>members.manage</c> would be requesting the ability to
    /// grant itself anything.
    /// </summary>
    [Theory]
    [InlineData(Scope.RolesManage)]
    [InlineData(Scope.MembersInvite)]
    [InlineData(Scope.MembersManage)]
    [InlineData(Scope.TenantSettings)]
    [InlineData(Scope.SharingManage)]
    [InlineData(Scope.SharingGuest)]
    [InlineData(Scope.AuditRead)]
    [InlineData(Scope.AuditManage)]
    public void AdministrationAtoms_AreMemberGrantableButNotOAuthRequestable(string atom)
    {
        Scope.PermissionAtoms.Should().Contain(atom,
            "a tenant role has to be able to confer {0}", atom);

        Scope.MemberGrantableScopes.Should().Contain(atom,
            "membership resolution has to preserve {0} rather than dropping it", atom);

        Scope.ValidRequestScopes.Should().NotContain(atom,
            "an OAuth client must not be able to request {0} at /authorize", atom);

        Scope.IsValid(atom).Should().BeFalse(
            "{0} must not pass client scope validation", atom);
    }

    /// <summary>
    /// A guest link is capped at read. Anything a guest grant may hold must be a read atom or the
    /// read alias, never a write tier or an administration atom.
    /// </summary>
    [Fact]
    public void AllowedGuestScopes_GrantNoWriteOrAdministrationAuthority()
    {
        Scope.AllowedGuestScopes.Should().NotBeEmpty();

        foreach (var atom in Scope.AllowedGuestScopes)
        {
            Scope.Satisfies(atom, Scope.GlucoseReadWrite).Should().BeFalse(
                "guest atom {0} must not confer write authority", atom);
            Scope.Satisfies(atom, Scope.MembersManage).Should().BeFalse(
                "guest atom {0} must not confer administration authority", atom);
            Scope.Satisfies(atom, Scope.FullAccess).Should().BeFalse(
                "guest atom {0} must not confer full access", atom);
        }
    }

    /// <summary>
    /// The public share surface is read-only for the same reason, and is reachable with no account
    /// behind it at all.
    /// </summary>
    [Fact]
    public void PublicShareScopes_GrantNoWriteOrAdministrationAuthority()
    {
        Scope.PublicShareScopes.Should().NotBeEmpty();

        foreach (var atom in Scope.PublicShareScopes)
        {
            Scope.Satisfies(atom, Scope.GlucoseReadWrite).Should().BeFalse(
                "public share atom {0} must not confer write authority", atom);
            Scope.Satisfies(atom, Scope.MembersManage).Should().BeFalse(
                "public share atom {0} must not confer administration authority", atom);
        }
    }

    /// <summary>
    /// The demo visitor's session is obtainable by anyone, so it must hold nothing that can change
    /// who else can get in. Member management is an escalation primitive: direct and role
    /// permissions are unioned into the effective set, so being able to edit either is being able
    /// to confer <see cref="Scope.FullAccess"/> on oneself.
    /// </summary>
    [Theory]
    [InlineData(Scope.MembersManage)]
    [InlineData(Scope.MembersInvite)]
    [InlineData(Scope.RolesManage)]
    [InlineData(Scope.SharingManage)]
    [InlineData(Scope.AuditRead)]
    [InlineData(Scope.FullAccess)]
    public void DemoVisitorPermissions_HoldNoEscalationPrimitive(string forbidden)
    {
        Scope.DemoVisitorPermissions.Should().NotBeEmpty();

        Scope.Satisfies(Scope.DemoVisitorPermissions, forbidden).Should().BeFalse(
            "anyone can obtain the demo visitor session, so it must not satisfy {0}", forbidden);
    }
}
