using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Injectables;

namespace Nocturne.API.Controllers;

/// <summary>
/// Controller for managing the injectable medication catalog.
/// Provides CRUD operations for user-defined insulin and GLP-1 medication entries.
/// </summary>
[ApiController]
[Route("api/v4/injectable-medications")]
[Produces("application/json")]
public class InjectableMedicationsController : ControllerBase
{
    private readonly IInjectableMedicationService _medicationService;
    private readonly ILogger<InjectableMedicationsController> _logger;

    public InjectableMedicationsController(
        IInjectableMedicationService medicationService,
        ILogger<InjectableMedicationsController> logger
    )
    {
        _medicationService = medicationService;
        _logger = logger;
    }

    /// <summary>
    /// Get preset medication templates for common insulins and GLP-1 agonists.
    /// Returns static reference data — no database call.
    /// </summary>
    [HttpGet("presets")]
    public ActionResult<IEnumerable<InjectableMedicationPreset>> GetPresets()
    {
        return Ok(InjectableMedicationPreset.GetAll());
    }

    /// <summary>
    /// Get all injectable medications in the catalog.
    /// </summary>
    /// <param name="includeArchived">Whether to include archived medications. Defaults to false.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of injectable medications.</returns>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<InjectableMedication>>> GetAll(
        [FromQuery] bool includeArchived = false,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var medications = await _medicationService.GetAllAsync(includeArchived, cancellationToken);
            return Ok(medications);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting injectable medications");
            return StatusCode(500, new { error = "An error occurred while retrieving medications" });
        }
    }

    /// <summary>
    /// Get a specific injectable medication by ID.
    /// </summary>
    /// <param name="id">The medication ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The requested medication if found.</returns>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InjectableMedication>> GetById(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var medication = await _medicationService.GetByIdAsync(id, cancellationToken);

            if (medication == null)
            {
                return NotFound(new { error = $"Injectable medication not found: {id}" });
            }

            return Ok(medication);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting injectable medication: {Id}", id);
            return StatusCode(500, new { error = "An error occurred while retrieving the medication" });
        }
    }

    /// <summary>
    /// Create a new injectable medication in the catalog.
    /// </summary>
    /// <param name="medication">The medication to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created medication with assigned ID.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(InjectableMedication), 201)]
    public async Task<ActionResult<InjectableMedication>> Create(
        [FromBody] InjectableMedication medication,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(medication.Name))
            {
                return BadRequest(new { error = "Medication name is required" });
            }

            var created = await _medicationService.CreateAsync(medication, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating injectable medication: {Name}", medication.Name);
            return StatusCode(500, new { error = "An error occurred while creating the medication" });
        }
    }

    /// <summary>
    /// Update an existing injectable medication.
    /// </summary>
    /// <param name="id">The medication ID to update.</param>
    /// <param name="medication">The updated medication data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated medication.</returns>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<InjectableMedication>> Update(
        Guid id,
        [FromBody] InjectableMedication medication,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (id != medication.Id && medication.Id != Guid.Empty)
            {
                return BadRequest(new { error = "ID in URL does not match ID in request body" });
            }

            // Ensure the ID from URL is used
            medication.Id = id;

            if (string.IsNullOrWhiteSpace(medication.Name))
            {
                return BadRequest(new { error = "Medication name is required" });
            }

            var updated = await _medicationService.UpdateAsync(medication, cancellationToken);
            return Ok(updated);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating injectable medication: {Id}", id);
            return StatusCode(500, new { error = "An error occurred while updating the medication" });
        }
    }

    /// <summary>
    /// Archive an injectable medication (soft delete).
    /// Archived medications are hidden from normal queries but can be restored.
    /// </summary>
    /// <param name="id">The medication ID to archive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Archive(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var success = await _medicationService.ArchiveAsync(id, cancellationToken);

            if (!success)
            {
                return NotFound(new { error = $"Injectable medication not found: {id}" });
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error archiving injectable medication: {Id}", id);
            return StatusCode(500, new { error = "An error occurred while archiving the medication" });
        }
    }
}
