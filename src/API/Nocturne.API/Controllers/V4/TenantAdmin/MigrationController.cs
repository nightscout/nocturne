using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.API.Controllers.V4.TenantAdmin;

/// <summary>
/// Migration endpoints for importing data from Nightscout.
/// </summary>
/// <seealso cref="IMigrationJobService"/>
/// <seealso cref="IConnectorConfigurationService"/>
[ApiController]
[Tags("TenantAdmin")]
[Route("api/v4/migration")]
public class MigrationController : ControllerBase
{
    private readonly IMigrationJobService _migrationService;
    private readonly IConnectorConfigurationService _connectorConfigService;
    private readonly ILogger<MigrationController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="MigrationController"/>.
    /// </summary>
    /// <param name="migrationService">Service for managing Nightscout data migration jobs.</param>
    /// <param name="connectorConfigService">Service for reading saved connector credentials.</param>
    /// <param name="logger">Logger instance.</param>
    public MigrationController(
        IMigrationJobService migrationService,
        IConnectorConfigurationService connectorConfigService,
        ILogger<MigrationController> logger)
    {
        _migrationService = migrationService;
        _connectorConfigService = connectorConfigService;
        _logger = logger;
    }

    /// <inheritdoc cref="IMigrationJobService.TestConnectionAsync"/>
    [HttpPost("test")]
    [RemoteForm]
    [ProducesResponseType(typeof(TestMigrationConnectionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TestMigrationConnectionResult>> TestConnection(
        [FromBody] TestMigrationConnectionRequest request,
        CancellationToken ct)
    {
        var result = await _migrationService.TestConnectionAsync(request, ct);
        return Ok(result);
    }

    /// <inheritdoc cref="IMigrationJobService.StartMigrationAsync"/>
    [HttpPost("start")]
    [RemoteForm]
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
                return Problem(detail: "Nightscout URL is required for API mode", statusCode: 400, title: "Bad Request");
            }
        }
        else
        {
            if (string.IsNullOrEmpty(request.MongoConnectionString))
            {
                return Problem(detail: "MongoDB connection string is required for MongoDB mode", statusCode: 400, title: "Bad Request");
            }
            if (string.IsNullOrEmpty(request.MongoDatabaseName))
            {
                return Problem(detail: "MongoDB database name is required for MongoDB mode", statusCode: 400, title: "Bad Request");
            }
        }

        var jobInfo = await _migrationService.StartMigrationAsync(request, ct);
        return AcceptedAtAction(nameof(GetStatus), new { jobId = jobInfo.Id }, jobInfo);
    }

    /// <summary>
    /// Start a migration using saved connector credentials (e.g., after Nightscout connector setup).
    /// </summary>
    [HttpPost("start-from-connector/{connectorName}")]
    [RemoteCommand]
    [ProducesResponseType(typeof(MigrationJobInfo), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MigrationJobInfo>> StartFromConnector(
        string connectorName, CancellationToken ct)
    {
        var config = await _connectorConfigService.GetConfigurationAsync(connectorName, ct);
        if (config is null)
        {
            return Problem(detail: $"No saved configuration found for connector '{connectorName}'", statusCode: 400, title: "Bad Request");
        }

        var secrets = await _connectorConfigService.GetSecretsAsync(connectorName, ct);

        var configJson = config.Configuration?.RootElement;
        var url = configJson?.GetProperty("url").GetString();
        var apiSecret = secrets.TryGetValue("apiSecret", out var s) ? s : null;

        if (string.IsNullOrEmpty(url))
        {
            return Problem(detail: "Nightscout URL not found in connector configuration", statusCode: 400, title: "Bad Request");
        }

        var request = new StartMigrationRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = url,
            NightscoutApiSecret = apiSecret,
        };

        var jobInfo = await _migrationService.StartMigrationAsync(request, ct);
        return AcceptedAtAction(nameof(GetStatus), new { jobId = jobInfo.Id }, jobInfo);
    }

    /// <inheritdoc cref="IMigrationJobService.GetStatusAsync"/>
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

    /// <inheritdoc cref="IMigrationJobService.CancelAsync"/>
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

    /// <inheritdoc cref="IMigrationJobService.GetHistoryAsync"/>
    [HttpGet("history")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IReadOnlyList<MigrationJobInfo>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MigrationJobInfo>>> GetHistory()
    {
        var history = await _migrationService.GetHistoryAsync();
        return Ok(history);
    }

    /// <inheritdoc cref="IMigrationJobService.GetPendingConfig"/>
    [HttpGet("pending-config")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PendingMigrationConfig), StatusCodes.Status200OK)]
    public ActionResult<PendingMigrationConfig> GetPendingConfig()
    {
        var config = _migrationService.GetPendingConfig();
        return Ok(config);
    }

    /// <inheritdoc cref="IMigrationJobService.GetSourcesAsync"/>
    [HttpGet("sources")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IReadOnlyList<MigrationSourceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MigrationSourceDto>>> GetSources(CancellationToken ct)
    {
        var sources = await _migrationService.GetSourcesAsync(ct);
        return Ok(sources);
    }
}

