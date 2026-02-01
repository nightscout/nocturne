using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for connector food entry imports.
/// </summary>
[ApiController]
[Route("api/v4/connector-food-entries")]
[Tags("V4 Connector Food Entries")]
public class ConnectorFoodEntriesController : ControllerBase
{
    private readonly IConnectorFoodEntryService _connectorFoodEntryService;

    public ConnectorFoodEntriesController(IConnectorFoodEntryService connectorFoodEntryService)
    {
        _connectorFoodEntryService = connectorFoodEntryService;
    }

    /// <summary>
    /// Import connector food entries.
    /// </summary>
    [HttpPost("import")]
    [Authorize]
    [ProducesResponseType(typeof(ConnectorFoodEntry[]), 200)]
    public async Task<ActionResult<ConnectorFoodEntry[]>> ImportEntries(
        [FromBody] ConnectorFoodEntryImport[] imports
    )
    {
        if (imports == null || imports.Length == 0)
        {
            return Ok(Array.Empty<ConnectorFoodEntry>());
        }

        var userId = HttpContext.GetSubjectIdString() ?? "default";

        var results = await _connectorFoodEntryService.ImportAsync(
            userId,
            imports,
            HttpContext.RequestAborted
        );

        return Ok(results.ToArray());
    }
}
