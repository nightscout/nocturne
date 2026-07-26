using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Projections;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// Controller for managing patient record data: clinical records, associated devices, and insulin configurations.
/// </summary>
/// <remarks>
/// Three resource types are exposed under <c>/api/v4/patient-record</c>:
/// <list type="bullet">
///   <item><description><b>Records</b> — top-level patient health record via <see cref="IPatientRecordRepository"/>.</description></item>
///   <item><description><b>Devices</b> — devices (pumps, CGMs) linked to the patient record via <see cref="IPatientDeviceRepository"/>.</description></item>
///   <item><description><b>Insulins</b> — insulin types configured for the patient via the insulin catalog and patient insulin repository.</description></item>
/// </list>
///
/// Write actions span two data categories, so each carries its own <see cref="RequireScopeAttribute"/>
/// rather than one controller-wide declaration: the patient record and its insulin configuration
/// (DIA, peak, curve — the inputs to the IOB calculation) are therapy settings, while the patient
/// device registry is the devices category. The class-level <c>[Authorize]</c> alone is satisfied by
/// read-only credentials such as a guest-link session.
/// </remarks>
/// <seealso cref="IPatientRecordRepository"/>
/// <seealso cref="IPatientDeviceRepository"/>
[ApiController]
[Tags("Health")]
[Route("api/v4/patient-record")]
[Authorize]
[Produces("application/json")]
public class PatientRecordController : ControllerBase
{
    private readonly IPatientRecordRepository _recordRepo;
    private readonly IPatientDeviceRepository _deviceRepo;
    private readonly IPatientInsulinRepository _insulinRepo;
    private readonly IDeviceService _deviceService;
    private readonly ISensorGlucoseRepository _sensorGlucoseRepo;
    private readonly IDeviceReattributionService _reattribution;

    public PatientRecordController(
        IPatientRecordRepository recordRepo,
        IPatientDeviceRepository deviceRepo,
        IPatientInsulinRepository insulinRepo,
        IDeviceService deviceService,
        ISensorGlucoseRepository sensorGlucoseRepo,
        IDeviceReattributionService reattribution)
    {
        _recordRepo = recordRepo;
        _deviceRepo = deviceRepo;
        _insulinRepo = insulinRepo;
        _deviceService = deviceService;
        _sensorGlucoseRepo = sensorGlucoseRepo;
        _reattribution = reattribution;
    }

    /// <summary>Window scanned for "discovered sources" — distinct unattributed streams seen recently.</summary>
    private static readonly TimeSpan DiscoveredSourceWindow = TimeSpan.FromDays(30);

    #region Patient Record

    /// <summary>
    /// Get or create the patient record
    /// </summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(PatientRecord), StatusCodes.Status200OK)]
    public async Task<ActionResult<PatientRecord>> GetPatientRecord(CancellationToken cancellationToken = default)
    {
        var record = await _recordRepo.GetOrCreateAsync(cancellationToken);
        return Ok(record);
    }

    /// <summary>
    /// Update the patient record
    /// </summary>
    [HttpPut]
    [RequireScope(OAuthScopes.TherapyReadWrite)]
    [RemoteForm(Invalidates = ["GetPatientRecord"])]
    [ProducesResponseType(typeof(PatientRecord), StatusCodes.Status200OK)]
    public async Task<ActionResult<PatientRecord>> UpdatePatientRecord(
        [FromBody] PatientRecord model,
        CancellationToken cancellationToken = default)
    {
        var updated = await _recordRepo.UpdateAsync(model, WriteOrigin.Live, cancellationToken);
        return Ok(updated);
    }

    #endregion

    #region Devices

    /// <summary>
    /// Get all patient devices
    /// </summary>
    [HttpGet("devices")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<PatientDevice>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PatientDevice>>> GetDevices(CancellationToken cancellationToken = default)
    {
        var devices = await _deviceRepo.GetAllAsync(cancellationToken);
        return Ok(devices);
    }

    /// <summary>
    /// Lists distinct <c>(DataSource, Device)</c> combinations seen in recent unattributed readings —
    /// candidate streams the user can register as devices from the settings UI.
    /// </summary>
    [HttpGet("devices/discovered-sources")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IReadOnlyList<DiscoveredSource>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DiscoveredSource>>> GetDiscoveredSources(CancellationToken cancellationToken = default)
    {
        var since = DateTime.UtcNow - DiscoveredSourceWindow;
        var sources = await _sensorGlucoseRepo.GetDiscoveredSourcesAsync(since, cancellationToken);
        return Ok(sources);
    }

    /// <summary>
    /// Create a new patient device
    /// </summary>
    [HttpPost("devices")]
    [RequireScope(OAuthScopes.DevicesReadWrite)]
    [RemoteForm(Invalidates = ["GetDevices", "GetDiscoveredSources"])]
    [ProducesResponseType(typeof(PatientDevice), StatusCodes.Status201Created)]
    public async Task<ActionResult<PatientDevice>> CreateDevice(
        [FromBody] PatientDevice model,
        CancellationToken cancellationToken = default)
    {
        await ResolveDeviceIdAsync(model, cancellationToken);
        var created = await _deviceRepo.CreateAsync(model, WriteOrigin.Live, cancellationToken);
        // Back-stamp existing unattributed readings this device now explains (glucose stream only).
        await _reattribution.ReattributeForDeviceAsync(created, cancellationToken);
        return CreatedAtAction(nameof(GetDevices), created);
    }

    /// <summary>
    /// Update a patient device
    /// </summary>
    [HttpPut("devices/{id:guid}")]
    [RequireScope(OAuthScopes.DevicesReadWrite)]
    [RemoteForm(Invalidates = ["GetDevices"])]
    [ProducesResponseType(typeof(PatientDevice), StatusCodes.Status200OK)]
    public async Task<ActionResult<PatientDevice>> UpdateDevice(
        Guid id,
        [FromBody] PatientDevice model,
        CancellationToken cancellationToken = default)
    {
        await ResolveDeviceIdAsync(model, cancellationToken);
        var updated = await _deviceRepo.UpdateAsync(id, model, WriteOrigin.Live, cancellationToken);
        return Ok(updated);
    }

    /// <summary>
    /// Delete a patient device
    /// </summary>
    [HttpDelete("devices/{id:guid}")]
    [RequireScope(OAuthScopes.DevicesReadWrite)]
    [RemoteCommand(Invalidates = ["GetDevices"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteDevice(Guid id, CancellationToken cancellationToken = default)
    {
        await _deviceRepo.DeleteAsync(id, WriteOrigin.Live, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Reassigns device priority in a single batch. Drag-to-reorder in the UI sends the full ordered
    /// list; each entry's position becomes its <see cref="PatientDevice.Rank"/>. One round trip instead
    /// of one PUT per device. An imperative command (no HTML form), mirroring the delete/restore surface.
    /// </summary>
    [HttpPost("devices/reorder")]
    [RequireScope(OAuthScopes.DevicesReadWrite)]
    [RemoteCommand(Invalidates = ["GetDevices"])]
    [ProducesResponseType(typeof(IEnumerable<PatientDevice>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PatientDevice>>> ReorderDevices(
        [FromBody] IReadOnlyList<DeviceRankAssignment> ranks,
        CancellationToken cancellationToken = default)
    {
        var changed = await _deviceRepo.ReorderAsync(
            ranks.Select(r => (r.Id, r.Rank)).ToList(), WriteOrigin.Live, cancellationToken);
        return Ok(changed);
    }

    private async Task ResolveDeviceIdAsync(PatientDevice model, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(model.SerialNumber))
        {
            model.DeviceId = await _deviceService.ResolveAsync(
                model.DeviceCategory,
                model.Manufacturer + " " + model.Model,
                model.SerialNumber,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ct);
        }
    }

    #endregion

    #region Insulins

    /// <summary>
    /// Get all patient insulins
    /// </summary>
    [HttpGet("insulins")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<PatientInsulin>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PatientInsulin>>> GetInsulins(CancellationToken cancellationToken = default)
    {
        var insulins = await _insulinRepo.GetAllAsync(cancellationToken);
        return Ok(insulins);
    }

    /// <summary>
    /// Create a new patient insulin
    /// </summary>
    [HttpPost("insulins")]
    [RequireScope(OAuthScopes.TherapyReadWrite)]
    [RemoteForm(Invalidates = ["GetInsulins"])]
    [ProducesResponseType(typeof(PatientInsulin), StatusCodes.Status201Created)]
    public async Task<ActionResult<PatientInsulin>> CreateInsulin(
        [FromBody] PatientInsulin model,
        CancellationToken cancellationToken = default)
    {
        var created = await _insulinRepo.CreateAsync(model, WriteOrigin.Live, cancellationToken);
        if (created.IsPrimary)
            await _insulinRepo.SetPrimaryAsync(created.Id, cancellationToken);
        return CreatedAtAction(nameof(GetInsulins), created);
    }

    /// <summary>
    /// Update a patient insulin
    /// </summary>
    [HttpPut("insulins/{id:guid}")]
    [RequireScope(OAuthScopes.TherapyReadWrite)]
    [RemoteForm(Invalidates = ["GetInsulins"])]
    [ProducesResponseType(typeof(PatientInsulin), StatusCodes.Status200OK)]
    public async Task<ActionResult<PatientInsulin>> UpdateInsulin(
        Guid id,
        [FromBody] PatientInsulin model,
        CancellationToken cancellationToken = default)
    {
        var updated = await _insulinRepo.UpdateAsync(id, model, WriteOrigin.Live, cancellationToken);
        if (updated.IsPrimary)
            await _insulinRepo.SetPrimaryAsync(updated.Id, cancellationToken);
        return Ok(updated);
    }

    /// <summary>
    /// Delete a patient insulin
    /// </summary>
    [HttpDelete("insulins/{id:guid}")]
    [RequireScope(OAuthScopes.TherapyReadWrite)]
    [RemoteCommand(Invalidates = ["GetInsulins"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteInsulin(Guid id, CancellationToken cancellationToken = default)
    {
        await _insulinRepo.DeleteAsync(id, WriteOrigin.Live, cancellationToken);
        return NoContent();
    }

    #endregion
}

/// <summary>A single device→rank assignment in a <see cref="PatientRecordController.ReorderDevices"/> batch.</summary>
/// <param name="Id">The patient device identifier.</param>
/// <param name="Rank">Its new priority (lower = higher priority).</param>
public sealed record DeviceRankAssignment(Guid Id, int Rank);
