using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Known insulin formulations. Read the full catalog, or the formulations in one category.
/// </summary>
/// <remarks>
/// The insulin catalog is a read-only in-memory registry of known insulin formulations
/// stored in <see cref="InsulinCatalog"/>. Entries can be filtered by <see cref="InsulinCategory"/>
/// (e.g., <c>RapidActing</c>, <c>LongActing</c>, <c>Intermediate</c>).
/// </remarks>
/// <seealso cref="InsulinCatalog"/>
/// <seealso cref="InsulinFormulation"/>
/// <seealso cref="InsulinCategory"/>
[ApiController]
[Route("api/v4/insulins")]
[Tags("Metadata")]
[Authorize]
public class InsulinCatalogController : ControllerBase
{
    /// <summary>
    /// Get all known insulin formulations.
    /// </summary>
    [HttpGet("catalog")]
    [RemoteQuery]
    public ActionResult<IReadOnlyList<InsulinFormulation>> GetCatalog()
    {
        return Ok(InsulinCatalog.GetAll());
    }

    /// <summary>
    /// Get insulin formulations filtered by category.
    /// </summary>
    [HttpGet("catalog/{category}")]
    public ActionResult<IReadOnlyList<InsulinFormulation>> GetCatalogByCategory(InsulinCategory category)
    {
        return Ok(InsulinCatalog.GetByCategory(category));
    }
}
