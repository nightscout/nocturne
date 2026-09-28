using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Tenant-level settings a tenant's own administrators control, as opposed to the platform-admin
/// surface in <c>PlatformAdmin.TenantController</c>.
/// </summary>
[ApiController]
[Tags("Identity")]
[Route("api/v4/tenant-settings")]
[Produces("application/json")]
[Authorize]
public class TenantSettingsController : ControllerBase
{
    private readonly ITenantService _tenantService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IPatientRecordRepository _patientRecords;

    public TenantSettingsController(
        ITenantService tenantService,
        ITenantAccessor tenantAccessor,
        IPatientRecordRepository patientRecords)
    {
        _tenantService = tenantService;
        _tenantAccessor = tenantAccessor;
        _patientRecords = patientRecords;
    }

    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(TenantSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TenantSettingsDto>> GetTenantSettings(CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.TenantSettings))
            return Forbid();

        return Ok(await _tenantService.GetSettingsAsync(_tenantAccessor.TenantId, ct));
    }

    // The demo's account is shared and obtainable without signing up, and it holds
    // tenant.settings — so a permission gate alone would let any visitor take the demo's own
    // reference down for everyone until the next reset.
    [DenyDemoSubject]
    [HttpPut("public-docs")]
    [RemoteCommand(Invalidates = ["GetTenantSettings"])]
    [ProducesResponseType(typeof(TenantSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TenantSettingsDto>> SetPublicDocs(
        [FromBody] SetPublicDocsRequest request, CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.TenantSettings))
            return Forbid();

        return Ok(await _tenantService.SetAllowPublicDocsAsync(
            _tenantAccessor.TenantId, request.Enabled, ct));
    }

    /// <summary>
    /// Who the tenant is for, as its onboarder answered, with the patient's preferred name.
    /// Owner-only: the answer describes the owner's own relationship to the patient.
    /// </summary>
    [HttpGet("patient-relationship")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PatientRelationshipDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PatientRelationshipDto>> GetPatientRelationship(CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.FullAccess))
            return Forbid();

        return Ok(await ReadPatientRelationshipAsync(ct));
    }

    /// <summary>
    /// Records who the tenant is for, overwriting any earlier answer. A non-blank
    /// <see cref="SetPatientRelationshipRequest.PatientName"/> becomes the patient record's
    /// preferred name; <see cref="PatientRelationship.Self"/> never touches it.
    /// </summary>
    [DenyDemoSubject]
    [HttpPut("patient-relationship")]
    [RemoteCommand(Invalidates = ["GetPatientRelationship", "PatientRecord_GetPatientRecord"])]
    [ProducesResponseType(typeof(PatientRelationshipDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PatientRelationshipDto>> SetPatientRelationship(
        [FromBody] SetPatientRelationshipRequest request, CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.FullAccess))
            return Forbid();

        await _tenantService.SetPatientRelationshipAsync(
            _tenantAccessor.TenantId, request.Relationship, ct);

        var name = request.PatientName?.Trim();
        if (request.Relationship != PatientRelationship.Self && !string.IsNullOrEmpty(name))
        {
            var record = await _patientRecords.GetOrCreateAsync(ct);
            record.PreferredName = name;
            await _patientRecords.UpdateAsync(record, WriteOrigin.Live, ct);
        }

        return Ok(await ReadPatientRelationshipAsync(ct));
    }

    private async Task<PatientRelationshipDto> ReadPatientRelationshipAsync(CancellationToken ct)
    {
        var relationship = await _tenantService.GetPatientRelationshipAsync(_tenantAccessor.TenantId, ct);
        var record = await _patientRecords.GetAsync(ct);
        return new PatientRelationshipDto(relationship, record?.PreferredName);
    }
}

public record SetPublicDocsRequest(bool Enabled);

public record SetPatientRelationshipRequest(
    [property: JsonRequired, EnumDataType(typeof(PatientRelationship))] PatientRelationship Relationship,
    [property: MaxLength(256)] string? PatientName = null);

public record PatientRelationshipDto(PatientRelationship? Relationship, string? PatientName);
