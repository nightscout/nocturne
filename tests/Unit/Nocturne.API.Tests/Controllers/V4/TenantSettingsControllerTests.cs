using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// The documentation opt-in is a tenant-administration setting, so it is gated on the same
/// <c>tenant.settings</c> atom the rest of that surface uses — a member who can read the tenant
/// must not be able to publish its API reference.
/// </summary>
public class TenantSettingsControllerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    [Fact]
    public async Task SetPublicDocs_RefusesAMemberWithoutTenantSettings()
    {
        var (controller, tenants) = Build(Scope.IdentityRead);

        var result = await controller.SetPublicDocs(new SetPublicDocsRequest(true), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        tenants.Verify(
            t => t.SetAllowPublicDocsAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetPublicDocs_WritesTheTenantsOwnFlag()
    {
        var (controller, tenants) = Build(Scope.TenantSettings);
        tenants.Setup(t => t.SetAllowPublicDocsAsync(TenantId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantSettingsDto(true));

        var result = await controller.SetPublicDocs(new SetPublicDocsRequest(true), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeEquivalentTo(new TenantSettingsDto(true));
        tenants.Verify(t => t.SetAllowPublicDocsAsync(TenantId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetPublicDocs_TurnsTheDocumentationSurfaceOffAgain()
    {
        var (controller, tenants) = Build(Scope.TenantSettings);
        tenants.Setup(t => t.SetAllowPublicDocsAsync(TenantId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantSettingsDto(false));

        await controller.SetPublicDocs(new SetPublicDocsRequest(false), CancellationToken.None);

        tenants.Verify(t => t.SetAllowPublicDocsAsync(TenantId, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTenantSettings_RefusesAMemberWithoutTenantSettings()
    {
        var (controller, tenants) = Build(Scope.IdentityRead);

        var result = await controller.GetTenantSettings(CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        tenants.Verify(
            t => t.GetSettingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetTenantSettings_ReadsTheResolvedTenant()
    {
        var (controller, tenants) = Build(Scope.TenantSettings);
        tenants.Setup(t => t.GetSettingsAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantSettingsDto(true));

        var result = await controller.GetTenantSettings(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeEquivalentTo(new TenantSettingsDto(true));
    }

    // Who the tenant is for describes the owner's own relationship to the patient, so running the
    // tenant (tenant.settings) is not enough to read or change it.
    [Fact]
    public async Task GetPatientRelationship_RefusesAnAdministratorWhoIsNotTheOwner()
    {
        var (controller, tenants) = Build(Scope.TenantSettings);

        var result = await controller.GetPatientRelationship(CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        tenants.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetPatientRelationship_RefusesAnAdministratorWhoIsNotTheOwner()
    {
        var (controller, tenants, records) = BuildWithRecords(Scope.TenantSettings, Scope.TherapyReadWrite);

        var result = await controller.SetPatientRelationship(
            new SetPatientRelationshipRequest(PatientRelationship.Caregiver, "Sam"), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        tenants.VerifyNoOtherCalls();
        records.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetPatientRelationship_ReadsTheResolvedTenantAndThePatientsName()
    {
        var (controller, tenants, records) = BuildWithRecords(Scope.FullAccess);
        tenants.Setup(t => t.GetPatientRelationshipAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PatientRelationship.Helper);
        records.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PatientRecord { PreferredName = "Sam" });

        var result = await controller.GetPatientRelationship(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().Be(new PatientRelationshipDto(PatientRelationship.Helper, "Sam"));
    }

    [Fact]
    public async Task GetPatientRelationship_IsUnsetBeforeAnyAnswer()
    {
        var (controller, tenants, records) = BuildWithRecords(Scope.FullAccess);
        tenants.Setup(t => t.GetPatientRelationshipAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientRelationship?)null);
        records.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PatientRecord?)null);

        var result = await controller.GetPatientRelationship(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().Be(new PatientRelationshipDto(null, null));
    }

    [Theory]
    [InlineData(PatientRelationship.Caregiver)]
    [InlineData(PatientRelationship.Helper)]
    public async Task SetPatientRelationship_SavesTheNameAsThePatientsPreferredName(PatientRelationship answer)
    {
        var (controller, tenants, records) = BuildAnswering(answer);
        var record = new PatientRecord { Pronouns = "they/them" };
        records.Setup(r => r.GetOrCreateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(record);
        records.Setup(r => r.UpdateAsync(record, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        records.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => record);

        var result = await controller.SetPatientRelationship(
            new SetPatientRelationshipRequest(answer, "  Sam "), CancellationToken.None);

        tenants.Verify(t => t.SetPatientRelationshipAsync(TenantId, answer, It.IsAny<CancellationToken>()), Times.Once);
        records.Verify(r => r.UpdateAsync(
            It.Is<PatientRecord>(p => p.PreferredName == "Sam" && p.Pronouns == "they/them"),
            WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().Be(new PatientRelationshipDto(answer, "Sam"));
    }

    [Theory]
    [InlineData(PatientRelationship.Self, "Sam")]
    [InlineData(PatientRelationship.Caregiver, null)]
    [InlineData(PatientRelationship.Helper, "   ")]
    public async Task SetPatientRelationship_LeavesThePatientRecordAloneWithoutANameToSave(
        PatientRelationship answer, string? name)
    {
        var (controller, tenants, records) = BuildAnswering(answer);
        records.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PatientRecord?)null);

        await controller.SetPatientRelationship(
            new SetPatientRelationshipRequest(answer, name), CancellationToken.None);

        tenants.Verify(t => t.SetPatientRelationshipAsync(TenantId, answer, It.IsAny<CancellationToken>()), Times.Once);
        records.Verify(r => r.GetOrCreateAsync(It.IsAny<CancellationToken>()), Times.Never);
        records.Verify(r => r.UpdateAsync(
            It.IsAny<PatientRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static (TenantSettingsController, Mock<ITenantService>, Mock<IPatientRecordRepository>) BuildAnswering(
        PatientRelationship answer)
    {
        var built = BuildWithRecords(Scope.FullAccess);
        built.Tenants.Setup(t => t.SetPatientRelationshipAsync(TenantId, answer, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        built.Tenants.Setup(t => t.GetPatientRelationshipAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(answer);
        return built;
    }

    private static (TenantSettingsController Controller, Mock<ITenantService> Tenants) Build(
        params string[] grantedScopes)
    {
        var (controller, tenants, _) = BuildWithRecords(grantedScopes);
        return (controller, tenants);
    }

    private static (TenantSettingsController Controller, Mock<ITenantService> Tenants, Mock<IPatientRecordRepository> Records)
        BuildWithRecords(params string[] grantedScopes)
    {
        var tenants = new Mock<ITenantService>(MockBehavior.Strict);
        var records = new Mock<IPatientRecordRepository>(MockBehavior.Strict);

        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.TenantId).Returns(TenantId);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);

        var controller = new TenantSettingsController(tenants.Object, accessor.Object, records.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        return (controller, tenants, records);
    }
}
