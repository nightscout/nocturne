using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// Pins <see cref="Scope.NormalizeForStorage"/>, the one form a granted scope set is written to a
/// credential in.
/// </summary>
[Trait("Category", "Unit")]
public class ScopeStorageFormTests
{
    [Fact]
    public void Full_access_is_stored_as_the_bare_atom()
    {
        Scope.NormalizeForStorage([Scope.FullAccess]).Should().Equal(Scope.FullAccess);

        // However the caller spells it, including an expansion read back off an older grant.
        Scope.NormalizeForStorage([.. Scope.AllScopes, Scope.FullAccess])
            .Should().Equal(Scope.FullAccess);
    }

    [Fact]
    public void The_bare_atom_and_its_expansion_authorize_identically()
    {
        // Which is the only reason collapsing is safe: every read path normalizes again.
        Scope.Normalize(Scope.NormalizeForStorage([Scope.FullAccess]))
            .Should().BeEquivalentTo(Scope.Normalize([.. Scope.AllScopes, Scope.FullAccess]));
    }

    [Fact]
    public void A_set_that_happens_to_cover_everything_is_not_full_access()
    {
        var stored = Scope.NormalizeForStorage(Scope.AllScopes);

        stored.Should().BeEquivalentTo(Scope.AllScopes);
        stored.Should().NotContain(Scope.FullAccess);
    }

    [Fact]
    public void Aliases_are_expanded_and_unknown_atoms_are_dropped()
    {
        var stored = Scope.NormalizeForStorage(
            [Scope.HealthRead, "api:entries:read", Scope.GlucoseRead]);

        stored.Should().Contain([Scope.GlucoseRead, Scope.TreatmentsRead, Scope.SleepRead]);
        stored.Should().NotContain([Scope.HealthRead, "api:entries:read"]);
        stored.Should().BeInAscendingOrder();
    }

    /// <summary>
    /// <c>MigrationJobService</c> stores what <see cref="ScopeTranslator.FromPermissions"/> gave it
    /// and never normalized the result. Going through <see cref="Scope.NormalizeForStorage"/> now
    /// only changes that if normalization can alter a translated set, so this pins that it cannot:
    /// every scope the translator emits is already an atom the vocabulary recognizes.
    /// </summary>
    [Theory]
    [InlineData("*")]
    [InlineData("admin")]
    [InlineData("api:*")]
    [InlineData("readable")]
    [InlineData("api:*:read")]
    [InlineData("*:*:read")]
    [InlineData("api:*:create")]
    [InlineData("api:*:update")]
    [InlineData("api:*:delete")]
    [InlineData("api:entries:read")]
    [InlineData("api:entries:*")]
    [InlineData("api:treatments:*")]
    [InlineData("api:devicestatus:*")]
    [InlineData("api:food:*")]
    [InlineData("api:profile:*")]
    [InlineData("api:activity:read")]
    [InlineData("api:activity:*")]
    public void Translating_a_legacy_permission_already_yields_a_normalized_set(string permission)
    {
        var translated = ScopeTranslator.FromPermissions([permission]);

        translated.Should().NotBeEmpty();
        Scope.Normalize(translated).Should().BeEquivalentTo(translated);
    }
}
