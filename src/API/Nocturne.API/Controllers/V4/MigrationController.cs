using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Services.Migration;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Migration endpoints for importing data from Nightscout
/// </summary>
[ApiController]
[Route("api/v4/migration")]
[Tags("Migration")]
public class MigrationController : ControllerBase
{
    private readonly IMigrationJobService _migrationService;
    private readonly ILogger<MigrationController> _logger;

    public MigrationController(
        IMigrationJobService migrationService,
        ILogger<MigrationController> logger)
    {
        _migrationService = migrationService;
        _logger = logger;
    }

    /// <summary>
    /// Test a migration source connection
    /// </summary>
    [HttpPost("test")]
    [RemoteCommand]
    [ProducesResponseType(typeof(TestMigrationConnectionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TestMigrationConnectionResult>> TestConnection(
        [FromBody] TestMigrationConnectionRequest request,
        CancellationToken ct)
    {
        var result = await _migrationService.TestConnectionAsync(request, ct);
        return Ok(result);
    }

    /// <summary>
    /// Start a new migration job
    /// </summary>
    [HttpPost("start")]
    [RemoteCommand]
    [ProducesResponseType(typeof(MigrationJobInfo), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MigrationJobInfo>> StartMigration(
        [FromBody] StartMigrationRequest request,
        CancellationToken ct)
    {
        // Validate request based on mode
        if (request.Mode == MigrationMode.Api)
        {
            if (string.IsNullOrEmpty(request.NightscoutUrl))
            {
                return BadRequest("Nightscout URL is required for API mode");
            }
        }
        else
        {
            if (string.IsNullOrEmpty(request.MongoConnectionString))
            {
                return BadRequest("MongoDB connection string is required for MongoDB mode");
            }
            if (string.IsNullOrEmpty(request.MongoDatabaseName))
            {
                return BadRequest("MongoDB database name is required for MongoDB mode");
            }
        }

        var jobInfo = await _migrationService.StartMigrationAsync(request, ct);
        return AcceptedAtAction(nameof(GetStatus), new { jobId = jobInfo.Id }, jobInfo);
    }

    /// <summary>
    /// Get the status of a migration job
    /// </summary>
    [HttpGet("{jobId:guid}/status")]
    [RemoteQuery]
    [ProducesResponseType(typeof(MigrationJobStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MigrationJobStatus>> GetStatus(Guid jobId)
    {
        try
        {
            var status = await _migrationService.GetStatusAsync(jobId);
            return Ok(status);
        }
        catch (KeyNotFoundException)
        {
            return NotFound($"Migration job {jobId} not found");
        }
    }

    /// <summary>
    /// Cancel a running migration job
    /// </summary>
    [HttpPost("{jobId:guid}/cancel")]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelMigration(Guid jobId)
    {
        try
        {
            await _migrationService.CancelAsync(jobId);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound($"Migration job {jobId} not found");
        }
    }

    /// <summary>
    /// Get migration job history
    /// </summary>
    [HttpGet("history")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IReadOnlyList<MigrationJobInfo>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MigrationJobInfo>>> GetHistory()
    {
        var history = await _migrationService.GetHistoryAsync();
        return Ok(history);
    }

    /// <summary>
    /// Get pending migration configuration from environment variables
    /// </summary>
    [HttpGet("pending-config")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PendingMigrationConfig), StatusCodes.Status200OK)]
    public ActionResult<PendingMigrationConfig> GetPendingConfig()
    {
        var config = _migrationService.GetPendingConfig();
        return Ok(config);
    }

    /// <summary>
    /// Get saved migration sources with their last migration timestamps
    /// </summary>
    [HttpGet("sources")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IReadOnlyList<MigrationSourceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MigrationSourceDto>>> GetSources(CancellationToken ct)
    {
        var sources = await _migrationService.GetSourcesAsync(ct);
        return Ok(sources);
    }
}

