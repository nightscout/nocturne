using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// <see cref="OAuthScope"/> restates <see cref="Scope.ValidRequestScopes"/> for the wire, because
/// the generated TypeScript client needs a typed handle on the vocabulary and an enum is the only
/// shape NSwag will emit as one. A restatement can drift, and did: <c>device.notify</c> and
/// <c>device.actuate</c> were requestable for months while absent from this enum, so the frontend
/// carried them as hand-written literals instead.
///
/// Each member carries the scope string TWICE — <see cref="EnumMemberAttribute"/>, which NSwag
/// reads to generate the client, and <c>JsonStringEnumMemberName</c>, which
/// <see cref="JsonStringEnumConverter{T}"/> uses to serialize the live response. Pinning only one
/// of the pair leaves the other free to drift, so the live API and the client it generated would
/// disagree with every test green. These tests pin the runtime serializer output as the source of
/// truth and require the OpenAPI attribute to agree with it.
/// </summary>
[Trait("Category", "Unit")]
public class OAuthScopeEnumConformanceTests
{
    /// <summary>The string the live API actually puts on the wire for a member.</summary>
    private static string SerializedValue(OAuthScope member) =>
        JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(member))!;

    private static string? OpenApiValue(OAuthScope member) =>
        typeof(OAuthScope)
            .GetField(member.ToString(), BindingFlags.Public | BindingFlags.Static)!
            .GetCustomAttribute<EnumMemberAttribute>()?.Value;

    private static string? JsonNameValue(OAuthScope member) =>
        typeof(OAuthScope)
            .GetField(member.ToString(), BindingFlags.Public | BindingFlags.Static)!
            .GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name;

    /// <summary>
    /// The invariant that would have caught the device.* drift. Asserted against what the
    /// serializer emits, not against an attribute, so it holds whichever attribute is wrong.
    /// </summary>
    [Fact]
    public void SerializedValues_AreExactlyTheRequestableVocabulary()
    {
        var members = Enum.GetValues<OAuthScope>();
        members.Should().NotBeEmpty();

        var onTheWire = members.Select(SerializedValue).ToHashSet(StringComparer.Ordinal);
        Scope.ValidRequestScopes.Should().NotBeEmpty();

        onTheWire.Should().BeEquivalentTo(
            Scope.ValidRequestScopes,
            "the enum exists only to restate the requestable vocabulary for the generated client; "
            + "a scope in one and not the other is drift, and the frontend is what breaks");
    }

    /// <summary>
    /// NSwag reads <see cref="EnumMemberAttribute"/> to generate the client while the response is
    /// serialized from <c>JsonStringEnumMemberName</c>. If the two disagree, the client is
    /// generated against a string the API never sends.
    /// </summary>
    [Fact]
    public void EveryMember_CarriesBothAttributes_AndTheyAgreeWithTheWire()
    {
        var members = Enum.GetValues<OAuthScope>();
        members.Should().NotBeEmpty();

        foreach (var member in members)
        {
            var openApi = OpenApiValue(member);
            var jsonName = JsonNameValue(member);
            var wire = SerializedValue(member);

            openApi.Should().NotBeNullOrWhiteSpace(
                "NSwag generates the TypeScript client from the EnumMember value, so {0} without "
                + "one would reach the client as its C# name", member);

            jsonName.Should().NotBeNullOrWhiteSpace(
                "the response is serialized from JsonStringEnumMemberName, so {0} without one "
                + "would go on the wire as its C# name", member);

            wire.Should().Be(openApi,
                "the client is generated from the OpenAPI value, so {0} serializing as something "
                + "else means the client is typed against a string the API never sends", member);

            jsonName.Should().Be(openApi,
                "{0} declares its scope string twice and the two copies must not drift", member);
        }
    }

    [Fact]
    public void SerializedValues_AreUnique()
    {
        var wireValues = Enum.GetValues<OAuthScope>().Select(SerializedValue).ToList();
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
        Enum.GetValues<OAuthScope>().Select(SerializedValue).Should().NotContain(atom,
            "{0} cannot be requested at /authorize, so it must not appear on the consent surface",
            atom);
    }

    /// <summary>
    /// Each member must serialize to the atom its NAME claims. The set-equality check above cannot
    /// see a transposition: swapping two members' attributes leaves the set, the per-member
    /// agreement and the uniqueness check all intact, while <c>OAuthScope.SleepRead</c> starts
    /// requesting <c>sleep.readwrite</c>. The frontend keys consent descriptions off the member
    /// name, so that reads as "View sleep data" over a write grant.
    /// </summary>
    [Fact]
    public void EveryMember_SerializesToTheAtomItsNameClaims()
    {
        var expected = new Dictionary<OAuthScope, string>
        {
            [OAuthScope.GlucoseRead] = Scope.GlucoseRead,
            [OAuthScope.GlucoseReadWrite] = Scope.GlucoseReadWrite,
            [OAuthScope.TreatmentsRead] = Scope.TreatmentsRead,
            [OAuthScope.TreatmentsReadWrite] = Scope.TreatmentsReadWrite,
            [OAuthScope.DevicesRead] = Scope.DevicesRead,
            [OAuthScope.DevicesReadWrite] = Scope.DevicesReadWrite,
            [OAuthScope.TherapyRead] = Scope.TherapyRead,
            [OAuthScope.TherapyReadWrite] = Scope.TherapyReadWrite,
            [OAuthScope.AlertsRead] = Scope.AlertsRead,
            [OAuthScope.AlertsReadWrite] = Scope.AlertsReadWrite,
            [OAuthScope.ReportsRead] = Scope.ReportsRead,
            [OAuthScope.IdentityRead] = Scope.IdentityRead,
            [OAuthScope.SharingReadWrite] = Scope.SharingReadWrite,
            [OAuthScope.HeartRateRead] = Scope.HeartRateRead,
            [OAuthScope.HeartRateReadWrite] = Scope.HeartRateReadWrite,
            [OAuthScope.StepCountRead] = Scope.StepCountRead,
            [OAuthScope.StepCountReadWrite] = Scope.StepCountReadWrite,
            [OAuthScope.SleepRead] = Scope.SleepRead,
            [OAuthScope.SleepReadWrite] = Scope.SleepReadWrite,
            [OAuthScope.FoodRead] = Scope.FoodRead,
            [OAuthScope.FoodReadWrite] = Scope.FoodReadWrite,
            [OAuthScope.DeviceNotify] = Scope.DeviceNotify,
            [OAuthScope.DeviceActuate] = Scope.DeviceActuate,
            [OAuthScope.HealthRead] = Scope.HealthRead,
            [OAuthScope.HealthReadWrite] = Scope.HealthReadWrite,
            [OAuthScope.FullAccess] = Scope.FullAccess,
        };

        expected.Keys.Should().BeEquivalentTo(
            Enum.GetValues<OAuthScope>(),
            "a member added to the enum without a row here would go unpinned, which is the gap "
            + "this test exists to close");

        foreach (var (member, atom) in expected)
        {
            SerializedValue(member).Should().Be(atom,
                "{0} must serialize to the atom its name claims, or a consent screen describes one "
                + "grant and requests another", member);
        }
    }
}
