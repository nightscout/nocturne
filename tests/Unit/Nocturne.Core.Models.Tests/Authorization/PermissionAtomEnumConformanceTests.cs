using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// <see cref="PermissionAtom"/> restates <see cref="Scope.PermissionAtoms"/> for the generated
/// TypeScript client, which the permission selector's guard test compares against. A restatement
/// can drift, so these pin it to the vocabulary in both directions and pin each member's name to
/// the atom it claims.
/// </summary>
[Trait("Category", "Unit")]
public class PermissionAtomEnumConformanceTests
{
    private static string SerializedValue(PermissionAtom member) =>
        JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(member))!;

    private static FieldInfo Field(PermissionAtom member) =>
        typeof(PermissionAtom).GetField(member.ToString(), BindingFlags.Public | BindingFlags.Static)!;

    [Fact]
    public void SerializedValues_AreExactlyThePermissionAtoms()
    {
        var onTheWire = Enum.GetValues<PermissionAtom>().Select(SerializedValue).ToList();
        Scope.PermissionAtoms.Should().NotBeEmpty();

        onTheWire.Should().OnlyHaveUniqueItems();
        onTheWire.Should().BeEquivalentTo(
            Scope.PermissionAtoms,
            "an atom in one and not the other is drift, and the selector guard compares against the enum");
    }

    [Fact]
    public void EveryMember_CarriesBothAttributes_AndTheyAgreeWithTheWire()
    {
        var members = Enum.GetValues<PermissionAtom>();
        members.Should().NotBeEmpty();

        foreach (var member in members)
        {
            var openApi = Field(member).GetCustomAttribute<EnumMemberAttribute>()?.Value;
            var jsonName = Field(member).GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name;

            openApi.Should().NotBeNullOrWhiteSpace("{0} would reach the client as its C# name", member);
            SerializedValue(member).Should().Be(openApi, "{0} must serialize to the value the client is generated from", member);
            jsonName.Should().Be(openApi, "{0} declares its atom twice and the copies must not drift", member);
        }
    }

    [Fact]
    public void EveryMember_SerializesToTheAtomItsNameClaims()
    {
        var expected = new Dictionary<PermissionAtom, string>
        {
            [PermissionAtom.GlucoseRead] = Scope.GlucoseRead,
            [PermissionAtom.GlucoseReadWrite] = Scope.GlucoseReadWrite,
            [PermissionAtom.TreatmentsRead] = Scope.TreatmentsRead,
            [PermissionAtom.TreatmentsReadWrite] = Scope.TreatmentsReadWrite,
            [PermissionAtom.DevicesRead] = Scope.DevicesRead,
            [PermissionAtom.DevicesReadWrite] = Scope.DevicesReadWrite,
            [PermissionAtom.HeartRateRead] = Scope.HeartRateRead,
            [PermissionAtom.HeartRateReadWrite] = Scope.HeartRateReadWrite,
            [PermissionAtom.StepCountRead] = Scope.StepCountRead,
            [PermissionAtom.StepCountReadWrite] = Scope.StepCountReadWrite,
            [PermissionAtom.SleepRead] = Scope.SleepRead,
            [PermissionAtom.SleepReadWrite] = Scope.SleepReadWrite,
            [PermissionAtom.FoodRead] = Scope.FoodRead,
            [PermissionAtom.FoodReadWrite] = Scope.FoodReadWrite,
            [PermissionAtom.ReportsRead] = Scope.ReportsRead,
            [PermissionAtom.TherapyRead] = Scope.TherapyRead,
            [PermissionAtom.TherapyReadWrite] = Scope.TherapyReadWrite,
            [PermissionAtom.AlertsRead] = Scope.AlertsRead,
            [PermissionAtom.AlertsReadWrite] = Scope.AlertsReadWrite,
            [PermissionAtom.AlertsAcknowledge] = Scope.AlertsAcknowledge,
            [PermissionAtom.IdentityRead] = Scope.IdentityRead,
            [PermissionAtom.RolesManage] = Scope.RolesManage,
            [PermissionAtom.MembersInvite] = Scope.MembersInvite,
            [PermissionAtom.MembersManage] = Scope.MembersManage,
            [PermissionAtom.TenantSettings] = Scope.TenantSettings,
            [PermissionAtom.SharingManage] = Scope.SharingManage,
            [PermissionAtom.SharingGuest] = Scope.SharingGuest,
            [PermissionAtom.AuditRead] = Scope.AuditRead,
            [PermissionAtom.AuditManage] = Scope.AuditManage,
            [PermissionAtom.DeviceNotify] = Scope.DeviceNotify,
            [PermissionAtom.DeviceActuate] = Scope.DeviceActuate,
        };

        expected.Keys.Should().BeEquivalentTo(Enum.GetValues<PermissionAtom>());

        foreach (var (member, atom) in expected)
        {
            SerializedValue(member).Should().Be(atom, "{0} must serialize to the atom its name claims", member);
        }
    }
}
