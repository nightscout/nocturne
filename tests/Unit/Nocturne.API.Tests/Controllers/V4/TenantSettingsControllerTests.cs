using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Identity;
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
    private static readonly Guid OwnerId = Guid.CreateVersion7();

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
            .ReturnsAsync(Settings(true));

        var result = await controller.SetPublicDocs(new SetPublicDocsRequest(true), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeEquivalentTo(Settings(true));
        tenants.Verify(t => t.SetAllowPublicDocsAsync(TenantId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetPublicDocs_TurnsTheDocumentationSurfaceOffAgain()
    {
        var (controller, tenants) = Build(Scope.TenantSettings);
        tenants.Setup(t => t.SetAllowPublicDocsAsync(TenantId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Settings(false));

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
            .ReturnsAsync(Settings(true));

        var result = await controller.GetTenantSettings(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeEquivalentTo(Settings(true));
    }

    [Fact]
    public async Task SetDisplayName_RefusesAMemberWithoutTenantSettings()
    {
        var (controller, tenants) = Build(Scope.IdentityRead);

        var result = await controller.SetDisplayName(
            new SetDisplayNameRequest { DisplayName = "Sam's site" }, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        tenants.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetDisplayName_RejectsABlankName(string name)
    {
        var (controller, tenants) = Build(Scope.TenantSettings);

        var result = await controller.SetDisplayName(
            new SetDisplayNameRequest { DisplayName = name }, CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        tenants.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetDisplayName_RenamesTheResolvedTenantWithTheTrimmedName()
    {
        var (controller, tenants) = Build(Scope.TenantSettings);
        var renamed = Settings(false) with { DisplayName = "Sam's site" };
        tenants.Setup(t => t.SetDisplayNameAsync(TenantId, "Sam's site", It.IsAny<CancellationToken>()))
            .ReturnsAsync(renamed);

        var result = await controller.SetDisplayName(
            new SetDisplayNameRequest { DisplayName = "  Sam's site " }, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(renamed);
        tenants.Verify(t => t.SetDisplayNameAsync(TenantId, "Sam's site", It.IsAny<CancellationToken>()), Times.Once);
    }

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
        var (controller, tenants, records, _) = BuildWithRecords(Scope.TenantSettings, Scope.TherapyReadWrite);

        var result = await controller.SetPatientRelationship(
            new SetPatientRelationshipRequest { Relationship = PatientRelationship.Caregiver, PatientName = "Sam" }, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        tenants.VerifyNoOtherCalls();
        records.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetPatientRelationship_ReadsTheResolvedTenantAndThePatientsName()
    {
        var (controller, tenants, records, _) = BuildWithRecords(Scope.FullAccess);
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
        var (controller, tenants, records, _) = BuildWithRecords(Scope.FullAccess);
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
            new SetPatientRelationshipRequest { Relationship = answer, PatientName = "  Sam " }, CancellationToken.None);

        tenants.Verify(t => t.SetPatientRelationshipAsync(TenantId, answer, OwnerId, It.IsAny<CancellationToken>()), Times.Once);
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
            new SetPatientRelationshipRequest { Relationship = answer, PatientName = name }, CancellationToken.None);

        tenants.Verify(t => t.SetPatientRelationshipAsync(TenantId, answer, OwnerId, It.IsAny<CancellationToken>()), Times.Once);
        records.Verify(r => r.GetOrCreateAsync(It.IsAny<CancellationToken>()), Times.Never);
        records.Verify(r => r.UpdateAsync(
            It.IsAny<PatientRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnitsAndTimezone_AreRefusedToAnAdministratorWhoIsNotTheOwner()
    {
        var (controller, units) = BuildForUnits(Scope.TenantSettings, Scope.TherapyReadWrite);

        (await controller.GetUnitsAndTimezone("en-AU", false, CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
        (await controller.SetUnitsAndTimezone(
                new SetUnitsAndTimezoneRequest { GlucoseUnits = "mmol", Timezone = "Australia/Sydney" }, CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
        units.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetUnitsAndTimezone_ReadsForTheSignedInOwner()
    {
        var (controller, units) = BuildForUnits(Scope.FullAccess);
        var answer = new UnitsAndTimezoneDto("mmol", "Australia/Sydney");
        units.Setup(u => u.GetAsync(OwnerId, "en-AU", true, It.IsAny<CancellationToken>())).ReturnsAsync(answer);

        var result = await controller.GetUnitsAndTimezone("en-AU", true, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(answer);
    }

    [Fact]
    public async Task SetUnitsAndTimezone_SavesTheOwnersAnswer()
    {
        var (controller, units) = BuildForUnits(Scope.FullAccess);
        var saved = new UnitsAndTimezoneDto("mmol", "Australia/Sydney");
        units.Setup(u => u.SetAsync(TenantId, OwnerId, "mmol", "Australia/Sydney", It.IsAny<CancellationToken>()))
            .ReturnsAsync(saved);

        var result = await controller.SetUnitsAndTimezone(
            new SetUnitsAndTimezoneRequest { GlucoseUnits = "mmol", Timezone = " Australia/Sydney " }, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(saved);
    }

    private static TenantSettingsDto Settings(bool allowPublicDocs) =>
        new(allowPublicDocs, "sam", "Sam", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static (TenantSettingsController, Mock<IUnitsAndTimezoneService>) BuildForUnits(params string[] grantedScopes)
    {
        var built = BuildWithRecords(grantedScopes);
        built.Controller.HttpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = OwnerId };
        return (built.Controller, built.Units);
    }

    private static (TenantSettingsController, Mock<ITenantService>, Mock<IPatientRecordRepository>) BuildAnswering(
        PatientRelationship answer)
    {
        var built = BuildWithRecords(Scope.FullAccess);
        built.Controller.HttpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = OwnerId };
        built.Tenants.Setup(t => t.SetPatientRelationshipAsync(TenantId, answer, OwnerId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        built.Tenants.Setup(t => t.GetPatientRelationshipAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(answer);
        return (built.Controller, built.Tenants, built.Records);
    }

    private static (TenantSettingsController Controller, Mock<ITenantService> Tenants) Build(
        params string[] grantedScopes)
    {
        var (controller, tenants, _, _) = BuildWithRecords(grantedScopes);
        return (controller, tenants);
    }

    private static (TenantSettingsController Controller, Mock<ITenantService> Tenants, Mock<IPatientRecordRepository> Records, Mock<IUnitsAndTimezoneService> Units)
        BuildWithRecords(params string[] grantedScopes)
    {
        var tenants = new Mock<ITenantService>(MockBehavior.Strict);
        var records = new Mock<IPatientRecordRepository>(MockBehavior.Strict);
        var units = new Mock<IUnitsAndTimezoneService>(MockBehavior.Strict);

        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.TenantId).Returns(TenantId);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);

        var controller = new TenantSettingsController(
            tenants.Object, accessor.Object, records.Object, units.Object, PolicyAllowing(false).Object,
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example" }))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        return (controller, tenants, records, units);
    }

    private static Mock<IPlatformPolicyRepository> PolicyAllowing(bool allowOwnerSlugChange)
    {
        var policy = new Mock<IPlatformPolicyRepository>();
        policy.Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPolicy(allowOwnerSlugChange));
        return policy;
    }

    private static (TenantSettingsController Controller, Mock<ITenantService> Tenants) BuildForSubdomain(
        bool policyAllows, bool isPlatformAdmin, params string[] grantedScopes)
    {
        var tenants = new Mock<ITenantService>();
        tenants.Setup(t => t.GetSettingsAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantSettingsDto(false, "old", "Old", DateTime.UtcNow));
        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.TenantId).Returns(TenantId);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, IsPlatformAdmin = isPlatformAdmin };

        var controller = new TenantSettingsController(
            tenants.Object, accessor.Object, Mock.Of<IPatientRecordRepository>(), Mock.Of<IUnitsAndTimezoneService>(),
            PolicyAllowing(policyAllows).Object, Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example" }))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        return (controller, tenants);
    }

    [Fact]
    public async Task An_owner_cannot_change_the_subdomain_while_the_platform_policy_forbids_it()
    {
        var (controller, tenants) = BuildForSubdomain(policyAllows: false, isPlatformAdmin: false, Scope.FullAccess);

        var result = await controller.ChangeSubdomain(new ChangeSubdomainRequest("new"), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        tenants.Verify(t => t.ChangeSlugAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var read = await controller.GetSubdomain(CancellationToken.None);
        ((TenantSubdomainDto)((OkObjectResult)read.Result!).Value!).CanChange.Should().BeFalse();
    }

    [Fact]
    public async Task An_owner_changes_the_subdomain_when_the_platform_policy_allows_it()
    {
        var (controller, tenants) = BuildForSubdomain(policyAllows: true, isPlatformAdmin: false, Scope.FullAccess);
        tenants.Setup(t => t.ChangeSlugAsync(TenantId, "new", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(true));
        tenants.Setup(t => t.GetSettingsAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantSettingsDto(false, "new", "Old", DateTime.UtcNow));

        var result = await controller.ChangeSubdomain(new ChangeSubdomainRequest("new"), CancellationToken.None);

        var dto = (TenantSubdomainDto)((OkObjectResult)result.Result!).Value!;
        dto.Address.Should().Be("https://new.nocturne.example");
    }

    [Fact]
    public async Task A_platform_admin_owner_changes_the_subdomain_whatever_the_policy()
    {
        var (controller, tenants) = BuildForSubdomain(policyAllows: false, isPlatformAdmin: true, Scope.FullAccess);
        tenants.Setup(t => t.ChangeSlugAsync(TenantId, "new", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(true));

        var result = await controller.ChangeSubdomain(new ChangeSubdomainRequest("new"), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task The_subdomain_reports_when_it_may_next_change()
    {
        var (controller, tenants) = BuildForSubdomain(policyAllows: true, isPlatformAdmin: false, Scope.FullAccess);
        var next = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
        tenants.Setup(t => t.GetSettingsAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantSettingsDto(false, "sam", "Sam", DateTime.UtcNow, next));

        var read = await controller.GetSubdomain(CancellationToken.None);

        ((TenantSubdomainDto)((OkObjectResult)read.Result!).Value!).NextChangeAt.Should().Be(next);
    }

    [Fact]
    public async Task A_member_who_is_not_an_owner_cannot_change_the_subdomain()
    {
        var (controller, _) = BuildForSubdomain(policyAllows: true, isPlatformAdmin: true, Scope.TenantSettings);

        var result = await controller.ChangeSubdomain(new ChangeSubdomainRequest("new"), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
    }
}
