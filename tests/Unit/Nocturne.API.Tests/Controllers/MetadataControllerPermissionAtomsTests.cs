using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

[Trait("Category", "Unit")]
public class MetadataControllerPermissionAtomsTests
{
    private static PermissionAtomsMetadata GetPermissionAtoms()
    {
        var result = new MetadataController().GetPermissionAtoms();

        return result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<PermissionAtomsMetadata>().Subject;
    }

    private static List<string> OnTheWire(PermissionAtomsMetadata metadata) =>
        metadata.AvailableAtoms
            .Select(atom => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(atom))!)
            .ToList();

    [Fact]
    public void GetPermissionAtoms_OffersExactlyTheGrantableAtoms()
    {
        var metadata = GetPermissionAtoms();

        OnTheWire(metadata).Should().BeEquivalentTo(Scope.PermissionAtoms);
        metadata.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GetPermissionAtoms_DoesNotOfferTheRetiredStatisticsAtom()
    {
        var atoms = OnTheWire(GetPermissionAtoms());

        atoms.Should().NotContain("statistics.read");
        atoms.Should().Contain(Scope.ReportsRead);
    }

    [Fact]
    public void EveryOfferedAtom_PassesTheGrantValidationARoleSaveRunsThrough()
    {
        var atoms = OnTheWire(GetPermissionAtoms());

        Scope.ValidateGrant(atoms, [Scope.FullAccess]).Should().BeNull();
        Scope.ValidateGrant(["statistics.read"], [Scope.FullAccess])!.Code
            .Should().Be(GrantCeilingViolation.UnknownPermission);
    }
}
