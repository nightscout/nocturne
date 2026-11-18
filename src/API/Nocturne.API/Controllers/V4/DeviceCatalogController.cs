using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Known pump and CGM hardware models. Read the full catalog, or the models in one category.
/// </summary>
/// <remarks>
/// The device catalog is a read-only in-memory registry of known pump and CGM hardware models
/// stored in <see cref="DeviceCatalog"/>. Entries can be filtered by <see cref="DeviceCategory"/>
/// (e.g., <c>Pump</c>, <c>Cgm</c>, <c>Meter</c>).
/// </remarks>
/// <seealso cref="DeviceCatalog"/>
/// <seealso cref="DeviceCatalogEntry"/>
/// <seealso cref="DeviceCategory"/>
[ApiController]
[Route("api/v4/devices")]
[Tags("Metadata")]
[Authorize]
public class DeviceCatalogController : ControllerBase
{
    /// <summary>
    /// Get all known device models.
    /// </summary>
    [HttpGet("catalog")]
    [RemoteQuery]
    public ActionResult<IReadOnlyList<DeviceCatalogEntry>> GetCatalog()
    {
        return Ok(DeviceCatalog.GetAll());
    }

    /// <summary>
    /// Get device models filtered by category.
    /// </summary>
    [HttpGet("catalog/{category}")]
    public ActionResult<IReadOnlyList<DeviceCatalogEntry>> GetCatalogByCategory(DeviceCategory category)
    {
        return Ok(DeviceCatalog.GetByCategory(category));
    }
}
