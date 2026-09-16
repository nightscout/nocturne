using Nocturne.Core.Models.Authorization;

namespace Nocturne.Infrastructure.Data.Tests.Entities;

/// <summary>
/// Covers <see cref="OAuthGrantEntity.AdoptedLegacyCredential"/>: the one way a credential the
/// instance did not mint becomes a grant.
/// </summary>
public class AdoptedLegacyCredentialTests
{
    [Fact]
    public void An_adopted_credential_is_a_direct_grant_marked_as_legacy()
    {
        var grant = OAuthGrantEntity.AdoptedLegacyCredential(
            Guid.CreateVersion7(), "xDrip on the old phone", [Scope.GlucoseRead],
            legacySecretHash: "sha1");

        grant.GrantType.Should().Be(OAuthGrantTypes.Direct);
        grant.IsMigrated.Should().BeTrue();
    }

    [Fact]
    public void No_caller_outside_the_entity_can_mark_a_grant_as_legacy()
    {
        var setter = typeof(OAuthGrantEntity)
            .GetProperty(nameof(OAuthGrantEntity.IsMigrated))!
            .SetMethod!;

        // The flag drives the rotation nudge and the "Legacy" badge, and a grant that carries it
        // without being a direct grant seeded from a Nightscout credential is a lie in both places.
        // Keeping the setter shut is what makes the factory the only way to raise it.
        setter.IsPublic.Should().BeFalse();
        setter.IsAssembly.Should().BeFalse();
        setter.IsFamily.Should().BeFalse();
    }

    [Fact]
    public void Only_the_credential_columns_the_caller_supplied_are_populated()
    {
        var secretOnly = OAuthGrantEntity.AdoptedLegacyCredential(
            Guid.CreateVersion7(), "Nightscout (migrated)", [Scope.HealthReadWrite],
            legacySecretHash: "sha1");

        secretOnly.TokenHash.Should().BeNull();
        secretOnly.LegacySecretHash.Should().Be("sha1");
        secretOnly.LegacyTokenDigest.Should().BeNull();

        var tokenAndDigest = OAuthGrantEntity.AdoptedLegacyCredential(
            Guid.CreateVersion7(), "Reader", [Scope.GlucoseRead],
            tokenHash: "sha256", legacyTokenDigest: new string('a', 40));

        tokenAndDigest.TokenHash.Should().Be("sha256");
        tokenAndDigest.LegacySecretHash.Should().BeNull();
        tokenAndDigest.LegacyTokenDigest.Should().Be(new string('a', 40));
    }

    [Fact]
    public void An_alias_is_expanded_but_full_access_is_not()
    {
        var health = OAuthGrantEntity.AdoptedLegacyCredential(
            Guid.CreateVersion7(), "Nightscout (migrated)", [Scope.HealthReadWrite],
            legacySecretHash: "sha1");

        health.Scopes.Should().Contain(
            [Scope.GlucoseReadWrite, Scope.TreatmentsReadWrite, Scope.DevicesReadWrite]);
        health.Scopes.Should().NotContain(Scope.FullAccess);

        var admin = OAuthGrantEntity.AdoptedLegacyCredential(
            Guid.CreateVersion7(), "Boss", [.. Scope.AllScopes, Scope.FullAccess],
            tokenHash: "sha256");

        admin.Scopes.Should().Equal(Scope.FullAccess);
    }

    [Fact]
    public void A_credential_with_nothing_to_match_is_refused()
    {
        var adopt = () => OAuthGrantEntity.AdoptedLegacyCredential(
            Guid.CreateVersion7(), "Reader", [Scope.GlucoseRead]);

        adopt.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_adopted_credential_is_not_bound_to_a_client()
    {
        var grant = OAuthGrantEntity.AdoptedLegacyCredential(
            Guid.CreateVersion7(), "Reader", [Scope.GlucoseRead], tokenHash: "sha256");

        grant.ClientEntityId.Should().BeNull();
        grant.ExpiresAt.Should().BeNull();
        grant.RevokedAt.Should().BeNull();
    }
}
