using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Services.V4;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Admin controller for triggering V4 backfill operations.
/// Decomposes all existing legacy entries and treatments into the v4 granular tables.
/// </summary>
[ApiController]
[Route("api/v4/admin")]
[RequireAdmin]
[Produces("application/json")]
[Tags("V4 Admin")]
public class BackfillController : ControllerBase
{
    private readonly V4BackfillService _backfillService;
    private readonly ILogger<BackfillController> _logger;
    private static readonly SemaphoreSlim BackfillLock = new(1, 1);

    public BackfillController(
        V4BackfillService backfillService,
        ILogger<BackfillController> logger)
    {
        _backfillService = backfillService;
        _logger = logger;
    }

    /// <summary>
    /// Trigger a full backfill of legacy entries and treatments into v4 granular tables.
    /// This operation is idempotent and safe to re-run. Records are matched by LegacyId
    /// to avoid creating duplicates. Only one backfill can run at a time.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Backfill result with counts of processed, failed, and skipped records</returns>
    [HttpPost("backfill")]
    [ProducesResponseType(typeof(BackfillResult), 200)]
    [ProducesResponseType(409)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<BackfillResult>> TriggerBackfill(CancellationToken ct)
    {
        if (!await BackfillLock.WaitAsync(0, ct))
        {
            return Conflict(new { error = "A backfill operation is already in progress" });
        }

        try
        {
            _logger.LogInformation("V4 backfill triggered via admin endpoint");
            var result = await _backfillService.BackfillAsync(ct);
            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("V4 backfill was cancelled");
            return StatusCode(499, new { status = "cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "V4 backfill failed");
            return StatusCode(500, new { error = "Backfill failed", message = ex.Message });
        }
        finally
        {
            BackfillLock.Release();
        }
    }
}
