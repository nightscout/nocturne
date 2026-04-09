using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Admin controller for managing data deduplication.
/// Provides endpoints to run deduplication jobs and check their status.
/// </summary>
[ApiController]
[Route("api/v4/admin/deduplication")]
[Produces("application/json")]
[Tags("V4 Deduplication")]
[AllowDuringSetup]
public class DeduplicationController : ControllerBase
{
    private readonly IDeduplicationService _deduplicationService;
    private readonly ILogger<DeduplicationController> _logger;

    public DeduplicationController(
        IDeduplicationService deduplicationService,
        ILogger<DeduplicationController> logger)
    {
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    /// <summary>
    /// Start a deduplication job to link related records from different data sources.
    /// The job runs in the background and can be monitored using the status endpoint.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Job ID for tracking progress</returns>
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

    /// <summary>
    /// Get the status of a deduplication job.
    /// </summary>
    /// <param name="jobId">The job ID returned from the run endpoint</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current status and progress of the job</returns>
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

    /// <summary>
    /// Cancel a running deduplication job.
    /// </summary>
    /// <param name="jobId">The job ID to cancel</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Whether the job was successfully cancelled</returns>
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

    /// <summary>
    /// Get linked records for a specific entry by its canonical group.
    /// </summary>
    /// <param name="entryId">The entry ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>All linked records in the same canonical group</returns>
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

    /// <summary>
    /// Get linked records for a specific treatment by its canonical group.
    /// </summary>
    /// <param name="treatmentId">The treatment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>All linked records in the same canonical group</returns>
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

    /// <summary>
    /// Get linked records for a specific state span by its canonical group.
    /// </summary>
    /// <param name="stateSpanId">The state span ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>All linked records in the same canonical group</returns>
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

    /// <summary>
    /// Get linked records for any V4 record type by its canonical group.
    /// </summary>
    /// <param name="recordType">The record type (e.g. SensorGlucose, Bolus, CarbIntake)</param>
    /// <param name="recordId">The record ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>All linked records in the same canonical group</returns>
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
/// Response for starting a deduplication job
/// </summary>
public class DeduplicationJobResponse
{
    public Guid JobId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string StatusUrl { get; set; } = string.Empty;
}

/// <summary>
/// Response for cancelling a job
/// </summary>
public class CancelJobResponse
{
    public Guid JobId { get; set; }
    public bool Cancelled { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response containing linked records for a canonical group
/// </summary>
public class LinkedRecordsResponse
{
    public Guid CanonicalId { get; set; }
    public RecordType RecordType { get; set; }
    public List<LinkedRecord> LinkedRecords { get; set; } = new();
}
