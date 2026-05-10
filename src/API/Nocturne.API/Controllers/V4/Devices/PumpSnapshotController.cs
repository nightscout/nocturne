using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Controllers.V4.Devices;

/// <summary>
/// Controller for read-only access to insulin pump snapshot data.
/// Exposes standard V4 read operations via <see cref="V4ReadOnlyControllerBase{TModel,TRepository}"/>.
/// </summary>
/// <remarks>
/// Pump snapshots capture the reported state of the insulin pump at a point in time
/// (reservoir level, active basal rate, delivery status, etc.). Records are written
/// by connector ingest pipelines and are not editable via the API.
/// </remarks>
/// <seealso cref="IPumpSnapshotRepository"/>
/// <seealso cref="PumpSnapshot"/>
[ApiController]
[Tags("Devices")]
[Route("api/v4/device-status/pump")]
[Authorize]
[Produces("application/json")]
public class PumpSnapshotController(IPumpSnapshotRepository repo)
    : V4ReadOnlyControllerBase<PumpSnapshot, IPumpSnapshotRepository>(repo);
