using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// V1 debug controller for development and diagnostics.
/// Provides basic database connectivity tests against the PostgreSQL data store.
/// </summary>
/// <seealso cref="IEntryStore"/>
[ApiController]
[Route("api/v1/[controller]")]
public class DebugController : ControllerBase
{
    private readonly IEntryStore _store;
    private readonly ILogger<DebugController> _logger;

    public DebugController(IEntryStore store, ILogger<DebugController> logger)
    {
        _store = store;
        _logger = logger;
    }

    [HttpGet("postgresql-test")]
    public async Task<IActionResult> TestPostgreSqlConnection()
    {
        try
        {
            _logger.LogInformation("Testing PostgreSQL connection");

            // Try to get recent entries as a connectivity check
            var entries = await _store.QueryAsync(new EntryQuery { Type = "sgv", Count = 1 });
            var firstEntry = entries.FirstOrDefault();

            return Ok(
                new
                {
                    DatabaseType = "PostgreSQL",
                    HasEntries = firstEntry != null,
                    SampleEntry = firstEntry,
                    Status = "Success",
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing PostgreSQL: {Message}", ex.Message);
            return StatusCode(
                500,
                new
                {
                    Error = ex.Message,
                    InnerError = ex.InnerException?.Message,
                    Status = "Failed",
                }
            );
        }
    }

    [HttpGet("entries-direct")]
    public async Task<IActionResult> GetEntriesDirect()
    {
        try
        {
            var entries = await _store.QueryAsync(new EntryQuery { Type = "sgv", Count = 5 });
            return Ok(entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting entries directly");
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}
