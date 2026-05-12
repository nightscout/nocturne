using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// Controller for connector food entry imports.
/// </summary>
/// <remarks>
/// Connector food entries are raw nutritional records imported from third-party health apps
/// (e.g. MyFitnessPal, Glooko) via their respective connector pipelines. They are distinct from
/// the V4 food catalog (<see cref="FoodsController"/>) and are processed into candidate meal
/// match suggestions by <see cref="IMealMatchingService"/> (see <see cref="MealMatchingController"/>).
///
/// The <c>POST /import</c> endpoint is authenticated and delegates to
/// <see cref="IConnectorFoodEntryService.ImportAsync"/> with the current subject ID.
/// </remarks>
/// <seealso cref="IConnectorFoodEntryService"/>
/// <seealso cref="ConnectorFoodEntry"/>
/// <seealso cref="MealMatchingController"/>
[ApiController]
[Tags("Treatments")]
[Route("api/v4/connector-food-entries")]
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
