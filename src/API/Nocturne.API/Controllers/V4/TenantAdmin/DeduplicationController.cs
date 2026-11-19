using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4.TenantAdmin;

/// <summary>
/// Data deduplication. Run a deduplication job, check or cancel it, and list the linked records in a
/// duplicate group.
/// </summary>
/// <seealso cref="IDeduplicationAdmin"/>
[ApiController]
[Tags("TenantAdmin")]
[Route("api/v4/admin/deduplication")]
[Produces("application/json")]
[AllowDuringSetup]
[RequireSuperuser]
[ErrorEnvelope]
public class DeduplicationController : ControllerBase
{
    private readonly IDeduplicationAdmin _deduplication;
    private readonly ILogger<DeduplicationController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DeduplicationController"/>.
    /// </summary>
    /// <param name="deduplication">Runs and follows full deduplication runs.</param>
    /// <param name="logger">Logger instance.</param>
    public DeduplicationController(
        IDeduplicationAdmin deduplication,
        ILogger<DeduplicationController> logger)
    {
        _deduplication = deduplication;
        _logger = logger;
    }

    /// <inheritdoc cref="IDeduplicationAdmin.StartRunAsync"/>
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
            var jobId = await _deduplication.StartRunAsync(cancellationToken);

            return Accepted(new DeduplicationJobResponse
            {
                JobId = jobId,
                Message = "Deduplication job started",
                StatusUrl = $"/api/v4/admin/deduplication/status/{jobId}"
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Problem(detail: ex.Message, statusCode: 403, title: "Forbidden");
        }
    }

    /// <inheritdoc cref="IDeduplicationAdmin.GetRunStatusAsync"/>
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

        var status = await _deduplication.GetRunStatusAsync(jobId, cancellationToken);

        if (status == null)
        {
            return Problem(detail: "Job not found", statusCode: 404, title: "Not Found");
        }

        return Ok(status);
    }

    /// <inheritdoc cref="IDeduplicationAdmin.CancelRunAsync"/>
    [HttpPost("cancel/{jobId:guid}")]
    [RemoteCommand(Invalidates = ["GetJobStatus"])]
    [ProducesResponseType(typeof(CancelJobResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CancelJobResponse>> CancelJob(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Cancelling deduplication job {JobId}", jobId);

        var cancelled = await _deduplication.CancelRunAsync(jobId, cancellationToken);

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

    /// <inheritdoc cref="IDeduplicationAdmin.GetLinkedRecordsAsync"/>
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
        if (!Guid.TryParse(recordId, out var recordGuid))
        {
            return Problem(detail: "Invalid record ID format", statusCode: 400, title: "Bad Request");
        }

        var linkedRecord = await _deduplication.GetLinkedRecordAsync(
            recordType, recordGuid, cancellationToken);

        if (linkedRecord == null)
        {
            return Problem(detail: "Record not found or not linked", statusCode: 404, title: "Not Found");
        }

        var allLinked = await _deduplication.GetLinkedRecordsAsync(
            linkedRecord.CanonicalId, cancellationToken);

        return Ok(new LinkedRecordsResponse
        {
            CanonicalId = linkedRecord.CanonicalId,
            RecordType = recordType,
            LinkedRecords = allLinked.ToList()
        });
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
