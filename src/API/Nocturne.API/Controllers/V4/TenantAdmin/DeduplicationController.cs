using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4.TenantAdmin;

/// <summary>
/// Admin controller for managing data deduplication.
/// Provides endpoints to run deduplication jobs and check their status.
/// </summary>
/// <seealso cref="IDeduplicationService"/>
[ApiController]
[Tags("TenantAdmin")]
[Route("api/v4/admin/deduplication")]
[Produces("application/json")]
[AllowDuringSetup]
public class DeduplicationController : ControllerBase
{
    private readonly IDeduplicationService _deduplicationService;
    private readonly ILogger<DeduplicationController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DeduplicationController"/>.
    /// </summary>
    /// <param name="deduplicationService">Service for running and querying deduplication jobs.</param>
    /// <param name="logger">Logger instance.</param>
    public DeduplicationController(
        IDeduplicationService deduplicationService,
        ILogger<DeduplicationController> logger)
    {
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    /// <inheritdoc cref="IDeduplicationService.StartDeduplicationJobAsync"/>
    [HttpPost("run")]
    [RemoteCommand]
    [ProducesResponseType(typeof(DeduplicationJobResponse), 202)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<DeduplicationJobResponse>> StartDeduplicationJob(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting deduplication job");

        try
        {
            var jobId = await _deduplicationService.StartDeduplicationJobAsync(cancellationToken);

            return Accepted(new DeduplicationJobResponse
            {
                JobId = jobId,
                Message = "Deduplication job started",
                StatusUrl = $"/api/v4/admin/deduplication/status/{jobId}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start deduplication job");
            return Problem(detail: "Failed to start deduplication job", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <inheritdoc cref="IDeduplicationService.GetJobStatusAsync"/>
    [HttpGet("status/{jobId:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(DeduplicationJobStatus), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<DeduplicationJobStatus>> GetJobStatus(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting status for deduplication job {JobId}", jobId);

        try
        {
            var status = await _deduplicationService.GetJobStatusAsync(jobId, cancellationToken);

            if (status == null)
            {
                return Problem(detail: "Job not found", statusCode: 404, title: "Not Found");
            }

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get status for job {JobId}", jobId);
            return Problem(detail: "Failed to get job status", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <inheritdoc cref="IDeduplicationService.CancelJobAsync"/>
    [HttpPost("cancel/{jobId:guid}")]
    [RemoteCommand]
    [ProducesResponseType(typeof(CancelJobResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CancelJobResponse>> CancelJob(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Cancelling deduplication job {JobId}", jobId);

        try
        {
            var cancelled = await _deduplicationService.CancelJobAsync(jobId, cancellationToken);

            if (!cancelled)
            {
                return Problem(detail: "Job not found or already completed", statusCode: 404, title: "Not Found");
            }

            return Ok(new CancelJobResponse
            {
                JobId = jobId,
                Cancelled = true,
                Message = "Job cancellation requested"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel job {JobId}", jobId);
            return Problem(detail: "Failed to cancel job", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <inheritdoc cref="IDeduplicationService.GetLinkedRecordsAsync"/>
    [HttpGet("entries/{entryId}/sources")]
    [RemoteQuery]
    [ProducesResponseType(typeof(LinkedRecordsResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<LinkedRecordsResponse>> GetEntryLinkedRecords(
        string entryId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Guid.TryParse(entryId, out var entryGuid))
            {
                return Problem(detail: "Invalid entry ID format", statusCode: 400, title: "Bad Request");
            }

            var linkedRecord = await _deduplicationService.GetLinkedRecordAsync(
                RecordType.Entry, entryGuid, cancellationToken);

            if (linkedRecord == null)
            {
                return Problem(detail: "Entry not found or not linked", statusCode: 404, title: "Not Found");
            }

            var allLinked = await _deduplicationService.GetLinkedRecordsAsync(
                linkedRecord.CanonicalId, cancellationToken);

            return Ok(new LinkedRecordsResponse
            {
                CanonicalId = linkedRecord.CanonicalId,
                RecordType = RecordType.Entry,
                LinkedRecords = allLinked.ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get linked records for entry {EntryId}", entryId);
            return Problem(detail: "Failed to get linked records", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <inheritdoc cref="IDeduplicationService.GetLinkedRecordsAsync"/>
    [HttpGet("treatments/{treatmentId}/sources")]
    [RemoteQuery]
    [ProducesResponseType(typeof(LinkedRecordsResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<LinkedRecordsResponse>> GetTreatmentLinkedRecords(
        string treatmentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Guid.TryParse(treatmentId, out var treatmentGuid))
            {
                return Problem(detail: "Invalid treatment ID format", statusCode: 400, title: "Bad Request");
            }

            var linkedRecord = await _deduplicationService.GetLinkedRecordAsync(
                RecordType.Treatment, treatmentGuid, cancellationToken);

            if (linkedRecord == null)
            {
                return Problem(detail: "Treatment not found or not linked", statusCode: 404, title: "Not Found");
            }

            var allLinked = await _deduplicationService.GetLinkedRecordsAsync(
                linkedRecord.CanonicalId, cancellationToken);

            return Ok(new LinkedRecordsResponse
            {
                CanonicalId = linkedRecord.CanonicalId,
                RecordType = RecordType.Treatment,
                LinkedRecords = allLinked.ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get linked records for treatment {TreatmentId}", treatmentId);
            return Problem(detail: "Failed to get linked records", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <inheritdoc cref="IDeduplicationService.GetLinkedRecordsAsync"/>
    [HttpGet("state-spans/{stateSpanId}/sources")]
    [RemoteQuery]
    [ProducesResponseType(typeof(LinkedRecordsResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<LinkedRecordsResponse>> GetStateSpanLinkedRecords(
        string stateSpanId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Guid.TryParse(stateSpanId, out var stateSpanGuid))
            {
                return Problem(detail: "Invalid state span ID format", statusCode: 400, title: "Bad Request");
            }

            var linkedRecord = await _deduplicationService.GetLinkedRecordAsync(
                RecordType.StateSpan, stateSpanGuid, cancellationToken);

            if (linkedRecord == null)
            {
                return Problem(detail: "State span not found or not linked", statusCode: 404, title: "Not Found");
            }

            var allLinked = await _deduplicationService.GetLinkedRecordsAsync(
                linkedRecord.CanonicalId, cancellationToken);

            return Ok(new LinkedRecordsResponse
            {
                CanonicalId = linkedRecord.CanonicalId,
                RecordType = RecordType.StateSpan,
                LinkedRecords = allLinked.ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get linked records for state span {StateSpanId}", stateSpanId);
            return Problem(detail: "Failed to get linked records", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <inheritdoc cref="IDeduplicationService.GetLinkedRecordsAsync"/>
    [HttpGet("records/{recordType}/{recordId}/sources")]
    [RemoteQuery]
    [ProducesResponseType(typeof(LinkedRecordsResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<LinkedRecordsResponse>> GetRecordLinkedRecords(
        RecordType recordType,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Guid.TryParse(recordId, out var recordGuid))
            {
                return Problem(detail: "Invalid record ID format", statusCode: 400, title: "Bad Request");
            }

            var linkedRecord = await _deduplicationService.GetLinkedRecordAsync(
                recordType, recordGuid, cancellationToken);

            if (linkedRecord == null)
            {
                return Problem(detail: "Record not found or not linked", statusCode: 404, title: "Not Found");
            }

            var allLinked = await _deduplicationService.GetLinkedRecordsAsync(
                linkedRecord.CanonicalId, cancellationToken);

            return Ok(new LinkedRecordsResponse
            {
                CanonicalId = linkedRecord.CanonicalId,
                RecordType = recordType,
                LinkedRecords = allLinked.ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get linked records for {RecordType} {RecordId}", recordType, recordId);
            return Problem(detail: "Failed to get linked records", statusCode: 500, title: "Internal Server Error");
        }
    }
}

/// <summary>
/// Response for starting a deduplication job.
/// </summary>
public class DeduplicationJobResponse
{
    /// <summary>Gets or sets the unique identifier assigned to the started job.</summary>
    public Guid JobId { get; set; }
    /// <summary>Gets or sets a human-readable status message.</summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>Gets or sets the relative URL to poll for job status.</summary>
    public string StatusUrl { get; set; } = string.Empty;
}

/// <summary>
/// Response for cancelling a deduplication job.
/// </summary>
public class CancelJobResponse
{
    /// <summary>Gets or sets the identifier of the cancelled job.</summary>
    public Guid JobId { get; set; }
    /// <summary>Gets or sets whether the cancellation was accepted.</summary>
    public bool Cancelled { get; set; }
    /// <summary>Gets or sets a human-readable message describing the outcome.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response containing linked records for a canonical deduplication group.
/// </summary>
public class LinkedRecordsResponse
{
    /// <summary>Gets or sets the canonical record identifier shared by all linked records.</summary>
    public Guid CanonicalId { get; set; }
    /// <summary>Gets or sets the record type of the canonical group.</summary>
    public RecordType RecordType { get; set; }
    /// <summary>Gets or sets the full list of records linked to the canonical group.</summary>
    public List<LinkedRecord> LinkedRecords { get; set; } = new();
}
