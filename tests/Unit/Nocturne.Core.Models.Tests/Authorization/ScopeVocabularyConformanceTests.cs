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
}
