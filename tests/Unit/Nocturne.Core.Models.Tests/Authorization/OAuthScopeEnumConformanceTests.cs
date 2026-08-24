using System.Reflection;
using System.Runtime.Serialization;
using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// <see cref="OAuthScope"/> restates <see cref="Scope.ValidRequestScopes"/> for the wire, because
/// the generated TypeScript client needs a typed handle on the vocabulary and an enum is the only
/// shape NSwag will emit as one. A restatement can drift, and did: <c>device.notify</c> and
/// <c>device.actuate</c> were requestable for months while absent from this enum, so the frontend
/// carried them as hand-written literals instead. These tests are what make the restatement safe.
/// </summary>
[Trait("Category", "Unit")]
public class OAuthScopeEnumConformanceTests
{
    private static IReadOnlyList<(OAuthScope Member, string WireValue)> Members()
    {
        return Enum.GetValues<OAuthScope>()
            .Select(member => (
                Member: member,
                WireValue: typeof(OAuthScope)
                    .GetField(member.ToString(), BindingFlags.Public | BindingFlags.Static)!
                    .GetCustomAttribute<EnumMemberAttribute>()!
                    .Value!))
            .ToList();
    }

    [Fact]
    public void EveryMember_CarriesAWireValue()
    {
        var members = Enum.GetValues<OAuthScope>();
        members.Should().NotBeEmpty();

        foreach (var member in members)
        {
            var attribute = typeof(OAuthScope)
                .GetField(member.ToString(), BindingFlags.Public | BindingFlags.Static)!
                .GetCustomAttribute<EnumMemberAttribute>();

            attribute.Should().NotBeNull(
                "{0} is serialized by its EnumMember value, so a member without one would go on the "
                + "wire as its C# name", member);
            attribute!.Value.Should().NotBeNullOrWhiteSpace();
        }
    }

    /// <summary>
    /// The invariant that would have caught the device.* drift.
    /// </summary>
    [Fact]
    public void WireValues_AreExactlyTheRequestableVocabulary()
    {
        var wireValues = Members().Select(m => m.WireValue).ToHashSet(StringComparer.Ordinal);
        wireValues.Should().NotBeEmpty();

        wireValues.Should().BeEquivalentTo(
            Scope.ValidRequestScopes,
            "the enum exists only to restate the requestable vocabulary for the generated client; "
            + "a scope in one and not the other is drift, and the frontend is what breaks");
    }

    [Fact]
    public void WireValues_AreUnique()
    {
        var wireValues = Members().Select(m => m.WireValue).ToList();
        wireValues.Should().NotBeEmpty();
        wireValues.Should().OnlyHaveUniqueItems(
            "two members sharing a wire value would deserialize ambiguously");
    }

    /// <summary>
    /// The tenant-administration atoms are deliberately absent: a client cannot request them, so
    /// offering them on the consent surface would be offering something that can never be granted.
    /// </summary>
    [Theory]
    [InlineData(Scope.RolesManage)]
    [InlineData(Scope.MembersManage)]
    [InlineData(Scope.MembersInvite)]
    [InlineData(Scope.TenantSettings)]
    [InlineData(Scope.SharingManage)]
    [InlineData(Scope.SharingGuest)]
    [InlineData(Scope.AuditRead)]
    [InlineData(Scope.AuditManage)]
    public void AdministrationAtoms_AreNotOnTheClientEnum(string atom)
    {
        Members().Select(m => m.WireValue).Should().NotContain(atom,
            "{0} cannot be requested at /authorize, so it must not appear on the consent surface",
            atom);
    }
}
