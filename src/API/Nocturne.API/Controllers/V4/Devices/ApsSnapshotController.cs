using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Controllers.V4.Devices;

/// <summary>
/// Controller for read-only access to APS (Artificial Pancreas System) loop algorithm snapshot data.
/// Exposes standard V4 read operations via <see cref="V4ReadOnlyControllerBase{TModel,TRepository}"/>.
/// </summary>
/// <remarks>
/// APS snapshots capture the real-time output of loop algorithm calculations (e.g., AAPS oref0/oref1
/// output) recorded at the time of each closed-loop decision. Records are written by connector
/// ingest pipelines and are not editable via the API.
/// </remarks>
/// <seealso cref="IApsSnapshotRepository"/>
/// <seealso cref="ApsSnapshot"/>
[ApiController]
[Tags("Devices")]
[Route("api/v4/device-status/aps")]
[Authorize]
[Produces("application/json")]
public class ApsSnapshotController(IApsSnapshotRepository repo)
    : V4ReadOnlyControllerBase<ApsSnapshot, IApsSnapshotRepository>(repo);
