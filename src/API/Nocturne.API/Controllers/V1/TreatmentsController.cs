using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Configuration;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Helpers;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Treatments controller that provides 1:1 compatibility with Nightscout treatments endpoints.
/// Implements the /api/v1/treatments/* endpoints from the legacy JavaScript implementation.
/// </summary>
/// <seealso cref="ITreatmentService"/>
/// <seealso cref="IDocumentProcessingService"/>
[ApiController]
[Tags("V1")]
[Route("api/v1/[controller]")]
[Authorize(Policy = PolicyNames.HasPermissions)]
public class TreatmentsController : ControllerBase
{
    private readonly ITreatmentService _treatmentService;
    private readonly IDocumentProcessingService _documentProcessingService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TreatmentsController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TreatmentsController"/>.
    /// </summary>
    /// <param name="treatmentService">Service handling treatment CRUD operations.</param>
    /// <param name="treatmentProcessingService">Service for async document ingestion and processing.</param>
    /// <param name="timeProvider">Clock for the legacy default find window.</param>
    /// <param name="logger">Logger instance.</param>
    public TreatmentsController(
        ITreatmentService treatmentService,
        IDocumentProcessingService treatmentProcessingService,
        TimeProvider timeProvider,
        ILogger<TreatmentsController> logger
    )
    {
        _treatmentService = treatmentService;
        _documentProcessingService = treatmentProcessingService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Get treatments with optional filtering and pagination
    /// </summary>
    /// <param name="find">MongoDB-style query filter for date range filtering</param>
    /// <param name="count">Maximum number of treatments to return (default: 100, or 1000 when a find is given)</param>
    /// <param name="skip">Number of treatments to skip for pagination (default: 0)</param>
    /// <param name="format">Output format (json, csv, tsv, txt)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of treatments ordered by most recent first</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v1/treatments")]
    [ProducesResponseType(typeof(Treatment[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    [RequireScope(Scope.TreatmentsRead)]
    public async Task<ActionResult> GetTreatments(
        [FromQuery] string? find = null,
        [FromQuery] int? count = null,
        [FromQuery] int skip = 0,
        [FromQuery] string? format = null,
        CancellationToken cancellationToken = default
    )
    {
        var findQuery = LegacyFindQueryString.Resolve(HttpContext?.Request, find);

        _logger.LogDebug(
            "Treatments endpoint requested with count: {Count}, skip: {Skip}, findQuery: {FindQuery} from {RemoteIpAddress}",
            count,
            skip,
            findQuery,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Handle count parameter for Nightscout compatibility:
            // - 0 or negative: return empty array (Nightscout behavior)
            if (count <= 0)
            {
                _logger.LogDebug("Returning empty array for count={Count}", count);
                return Ok(Array.Empty<Treatment>());
            }

            // Validate skip parameter (negative is not valid)
            if (skip < 0)
            {
                skip = 0; // Normalize to 0 for Nightscout compatibility
            }

            var treatments = await _treatmentService.GetTreatmentsAsync(
                find: LegacyFindDateWindow.Apply(findQuery, _timeProvider.GetUtcNow()),
                count: count is { } requested ? LegacyReadLimits.ClampCount(requested) : null,
                skip: skip,
                cancellationToken: cancellationToken
            );
            var treatmentArray = treatments.ToArray();

            _logger.LogDebug("Successfully retrieved {Count} treatments", treatmentArray.Length);

            // Set response headers for caching
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.ToString("R");

            return Ok(treatmentArray);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving treatments");
            return StatusCode(500, "Internal server error while retrieving treatments");
        }
    }

    /// <summary>
    /// Get a specific treatment by ID
    /// </summary>
    /// <param name="id">Treatment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The treatment with the specified ID</returns>
    [HttpGet("{id}")]
    [NightscoutEndpoint("/api/v1/treatments/:id")]
    [ProducesResponseType(typeof(Treatment), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    [RequireScope(Scope.TreatmentsRead)]
    public async Task<ActionResult<Treatment>> GetTreatmentById(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Treatment by ID endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Invalid treatment ID: {Id}", id);
                return BadRequest("Treatment ID cannot be null or empty");
            }

            var treatment = await _treatmentService.GetTreatmentByIdAsync(id, cancellationToken);

            if (treatment == null)
            {
                _logger.LogDebug("Treatment not found with ID: {Id}", id);
                return NotFound($"Treatment with ID '{id}' not found");
            }

            _logger.LogDebug("Successfully retrieved treatment with ID: {Id}", id);

            // Set response headers for caching
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.ToString("R");

            return Ok(treatment);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving treatment with ID: {Id}", id);
            return StatusCode(500, $"Internal server error while retrieving treatment {id}");
        }
    }

    /// <summary>
    /// Create new treatments
    /// </summary>
    /// <param name="treatments">Treatments to create (can be single object or array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created treatments with assigned IDs</returns>
    [HttpPost]
    [Authorize]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [NightscoutEndpoint("/api/v1/treatments")]
    [ProducesResponseType(typeof(Treatment[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Treatment[]>> CreateTreatments(
        [FromBody] JsonElement treatments,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Create treatments endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        List<Treatment>? treatmentsToCreate;
        try
        {
            treatmentsToCreate = ReadTreatments(treatments);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in create treatments request");
            return BadRequest("Invalid JSON format");
        }

        try
        {
            if (treatmentsToCreate is null)
            {
                _logger.LogWarning("Invalid JSON format for treatments");
                return BadRequest("Invalid JSON format. Expected object or array of treatments.");
            }
            if (treatmentsToCreate.Count == 0)
            {
                _logger.LogWarning("No treatments provided for creation");
                return BadRequest("No treatments provided");
            }

            var resultArray = await CreateOrReplaceAsync(
                PrepareTreatments(treatmentsToCreate),
                cancellationToken
            );

            _logger.LogDebug("Successfully created {Count} treatments", resultArray.Length);

            // Return 200 OK to match Nightscout behavior (not 201 Created)
            return Ok(resultArray);
        }
        catch (TreatmentBatchFailedException ex)
        {
            // cgm-remote-monitor answers a failed insert with sendJSONStatus(res, 500, 'Mongo Error', err).
            return StatusCode(500, new { status = 500, message = "Mongo Error", description = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating treatments");
            return StatusCode(500, "Internal server error while creating treatments");
        }
    }

    /// <summary>
    /// Save the treatment identified by the <c>identifier</c> or <c>_id</c> in the body, inserting it
    /// when that id is not already stored
    /// </summary>
    /// <remarks>
    /// Nightscout's save takes one document and matches on <c>identifier</c> before <c>_id</c>; see
    /// <see cref="SaveAsync"/> for the full match order.
    /// </remarks>
    /// <param name="treatment">Treatment to save, as a single object</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saved treatment</returns>
    [HttpPut]
    [Authorize]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [NightscoutEndpoint("/api/v1/treatments")]
    [ProducesResponseType(typeof(Treatment), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Treatment>> SaveTreatments(
        [FromBody] JsonElement treatment,
        CancellationToken cancellationToken = default
    )
    {
        if (treatment.ValueKind != JsonValueKind.Object)
            return BadRequest("Invalid treatment payload. Expected an object.");

        try
        {
            return await SaveAsync(ReadTreatment(treatment)!, cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in save treatment request");
            return BadRequest("Invalid JSON format");
        }
    }

    /// <summary>
    /// Save a treatment by ID, inserting it when the ID is not already stored
    /// </summary>
    /// <param name="id">Treatment ID to save</param>
    /// <param name="treatment">Treatment data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Saved treatment</returns>
    [HttpPut("{id}")]
    [Authorize]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [NightscoutEndpoint("/api/v1/treatments/:id")]
    [ProducesResponseType(typeof(Treatment), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Treatment>> UpdateTreatment(
        string id,
        [FromBody] Treatment treatment,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Update treatment endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Invalid treatment ID: {Id}", id);
                return BadRequest("Treatment ID cannot be null or empty");
            }

            if (treatment == null)
            {
                _logger.LogWarning("Treatment data is null for update");
                return BadRequest("Treatment data cannot be null");
            }

            treatment.Id = id;
            return await SaveAsync(treatment, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating treatment with ID: {Id}", id);
            return StatusCode(500, $"Internal server error while updating treatment {id}");
        }
    }

    /// <summary>
    /// Delete a treatment by ID
    /// </summary>
    /// <param name="id">Treatment ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status</returns>
    [HttpDelete("{id}")]
    [Authorize]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [NightscoutEndpoint("/api/v1/treatments/:id")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteTreatment(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Delete treatment endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Invalid treatment ID: {Id}", id);
                return BadRequest("Treatment ID cannot be null or empty");
            }

            if (id == LegacyDeleteStatus.AnyId)
            {
                return HttpContext?.HasScope(Scope.FullAccess) == true
                    ? await BulkDeleteTreatments(cancellationToken)
                    : Forbid();
            }

            var deleted = await _treatmentService.DeleteTreatmentAsync(id, cancellationToken);

            _logger.LogDebug("Deleted treatment with ID {Id}: {Deleted}", id, deleted);

            return Ok(LegacyDeleteStatus.For(deleted ? 1 : 0));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting treatment with ID: {Id}", id);
            return StatusCode(500, $"Internal server error while deleting treatment {id}");
        }
    }

    /// <summary>
    /// Bulk delete treatments using query parameters
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of treatments deleted</returns>
    [HttpDelete]
    [Authorize]
    [RequireScope(Scope.FullAccess)]
    [NightscoutEndpoint("/api/v1/treatments")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> BulkDeleteTreatments(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Bulk delete treatments endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var find = LegacyFindQueryString.Resolve(HttpContext?.Request, boundFind: null);

            if (!LegacyFindQueryString.HasConditions(find))
            {
                _logger.LogWarning("Bulk delete requested without find query - this is dangerous");
                return BadRequest(
                    "Find query parameter is required for bulk delete to prevent accidental data loss"
                );
            }

            var deletedCount = await _treatmentService.DeleteTreatmentsAsync(
                LegacyFindDateWindow.Apply(find, _timeProvider.GetUtcNow()),
                cancellationToken
            );

            _logger.LogDebug("Successfully deleted {Count} treatments", deletedCount);

            return Ok(LegacyDeleteStatus.For(deletedCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk deleting treatments");
            return StatusCode(500, "Internal server error while bulk deleting treatments");
        }
    }

    /// <summary>
    /// Nightscout's save is an upsert. The treatment replaces the stored one its id names, else the
    /// one its <c>syncIdentifier</c> names, else, when it carries no id, the one stored at the same
    /// <c>created_at</c> with the same <c>eventType</c>. Anything else goes through the create path,
    /// which keeps the client's id.
    /// </summary>
    /// <remarks>
    /// A treatment the user deleted is not brought back: the save answers 200 with an empty array, as
    /// it saved nothing. Loop counts any other status as a failed upload and retries the whole carb
    /// batch, so a refusal would stall its later uploads.
    /// </remarks>
    private async Task<ActionResult<Treatment>> SaveAsync(
        Treatment treatment,
        CancellationToken cancellationToken
    )
    {
        var prepared = PrepareTreatments([treatment]).Single();
        try
        {
            return await SavePreparedAsync(prepared, cancellationToken);
        }
        catch (TreatmentRefusedException ex)
        {
            // NIGHTSCOUT-COMPAT: as for a refused create, see TreatmentEcho.
            return Ok(TreatmentEcho.Refused(prepared, ex.Message));
        }
    }

    private async Task<ActionResult<Treatment>> SavePreparedAsync(
        Treatment prepared,
        CancellationToken cancellationToken
    )
    {
        var keys = new[] { prepared.Id, prepared.SyncIdentifier }
            .OfType<string>()
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var key in keys)
        {
            if (await _treatmentService.UpdateTreatmentAsync(key, prepared, cancellationToken) is { } updated)
                return Ok(updated);
        }

        if (string.IsNullOrWhiteSpace(prepared.Id)
            && await _treatmentService.FindSameEventIdAsync(prepared, cancellationToken) is { } sameEventId
            && await _treatmentService.UpdateTreatmentAsync(sameEventId, prepared, cancellationToken) is { } replaced)
            return Ok(replaced);

        foreach (var key in keys)
        {
            if (await _treatmentService.IsTreatmentDeletedByUserAsync(key, cancellationToken))
                return NothingSaved(key);
        }

        var created = await _treatmentService.CreateTreatmentsAsync([prepared], cancellationToken: cancellationToken);
        if (created.SkippedDeleted > 0)
            return NothingSaved(prepared.Id);

        // NIGHTSCOUT-COMPAT: Nightscout stores any document, so v1 clients take any 2xx as stored
        // and a 4xx or 5xx as a reason to resend forever. A treatment Nocturne cannot store is
        // answered 200 with the treatment echoed under TreatmentEcho's marker and kept in the
        // rejected uploads for the owner to see.
        return Ok(TreatmentEcho.InSubmittedOrder(created)[0]);
    }

    /// <summary>
    /// Stores each document, replacing the stored treatment at its <c>created_at</c> with its
    /// <c>eventType</c> when it carries no identity, as Nightscout's upsert does.
    /// </summary>
    /// <remarks>
    /// Documents with an identity are written in runs, so a replaced treatment is stored before a later
    /// document of the same request looks for it.
    /// </remarks>
    private async Task<Treatment[]> CreateOrReplaceAsync(
        List<Treatment> treatments,
        CancellationToken cancellationToken
    )
    {
        var results = new List<Treatment>(treatments.Count);
        var withIdentity = new List<Treatment>();

        async Task FlushAsync()
        {
            if (withIdentity.Count == 0)
                return;

            results.AddRange(TreatmentEcho.InSubmittedOrder(
                await _treatmentService.CreateV1TreatmentsAsync(withIdentity, cancellationToken: cancellationToken)));
            withIdentity.Clear();
        }

        foreach (var treatment in treatments)
        {
            if (!string.IsNullOrWhiteSpace(treatment.Id) || !string.IsNullOrWhiteSpace(treatment.SyncIdentifier))
            {
                withIdentity.Add(treatment);
                continue;
            }

            await FlushAsync();

            if (await _treatmentService.FindSameEventIdAsync(treatment, cancellationToken) is { } sameEventId
                && await _treatmentService.UpdateTreatmentAsync(sameEventId, treatment, cancellationToken) is { } replaced)
            {
                results.Add(replaced);
                continue;
            }

            results.AddRange(TreatmentEcho.InSubmittedOrder(
                await _treatmentService.CreateV1TreatmentsAsync([treatment], cancellationToken: cancellationToken)));
        }

        await FlushAsync();
        return results.ToArray();
    }

    private static Treatment? ReadTreatment(JsonElement document)
    {
        var treatment = NightscoutJsonBody.ReadObject<Treatment>(document);
        if (treatment is not null
            && document.ValueKind == JsonValueKind.Object
            && document.TryGetProperty("identifier", out var identifier)
            && identifier.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(identifier.GetString()))
            treatment.Id = identifier.GetString();

        return treatment;
    }

    private OkObjectResult NothingSaved(string? id)
    {
        _logger.LogDebug("Refused save of treatment {Id}: the user deleted it", id);
        return Ok(Array.Empty<Treatment>());
    }

    /// <summary>
    /// The documents of an object or array body, each keyed on its <c>identifier</c> when it has one;
    /// null for any other body. A string escaping an unpaired surrogate, which Nightscout's JSON.parse
    /// accepts and System.Text.Json cannot read, is read with the escape replaced by U+FFFD rather
    /// than answered as invalid JSON, which an uploader would resend forever.
    /// </summary>
    private static List<Treatment>? ReadTreatments(JsonElement body)
    {
        try
        {
            return NightscoutJsonBody.ReadObjects(body, ReadTreatment);
        }
        catch (JsonException ex) when (ex.InnerException is InvalidOperationException)
        {
            using var repaired = JsonDocument.Parse(StorableText.RepairJsonEscapes(body.GetRawText()));
            return NightscoutJsonBody.ReadObjects(repaired.RootElement.Clone(), ReadTreatment);
        }
    }

    private List<Treatment> PrepareTreatments(List<Treatment> treatments)
    {
        var processed = _documentProcessingService.ProcessDocuments(treatments).ToList();

        foreach (var treatment in processed.Where(t => string.IsNullOrWhiteSpace(t.EventType)))
        {
            treatment.EventType = (treatment.Insulin.HasValue, treatment.Carbs.HasValue) switch
            {
                (true, true) => "Meal Bolus",
                (true, false) => "Correction Bolus",
                (false, true) => "Carb Correction",
                _ => "Note",
            };
        }

        return processed;
    }
}
