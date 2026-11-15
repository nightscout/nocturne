using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Models.Responses;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models.Authorization;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// The patient's timezone, which every report cuts its days on.
/// </summary>
/// <remarks>
/// Apart from <see cref="PatientRecordController"/>, whose <c>[Authorize]</c> refuses a public share:
/// a share's report windows must be cut on the same days the API buckets on. A member who may read
/// reports but not therapy data needs it too. The zone alone says nothing clinical.
/// </remarks>
[ApiController]
[Tags("Health")]
[Route("api/v4/patient-record/timezone")]
[Produces("application/json")]
public class PatientTimezoneController : ControllerBase
{
    /// <summary>The patient record's zone, else the active therapy profile's.</summary>
    /// <remarks>A share reads it through <see cref="IShareClockReader"/>.</remarks>
    [HttpGet]
    [RequireScope(Scope.GlucoseRead, Scope.ReportsRead, Scope.TherapyRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(PatientTimezoneResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PatientTimezoneResponse>> GetPatientTimezone(
        [FromServices] ITherapySettingsResolver therapySettings,
        [FromServices] ICategoryReadContext categoryReadContext,
        [FromServices] IShareClockReader shareClockReader,
        CancellationToken cancellationToken = default)
    {
        var timezone = categoryReadContext.IsShare
            ? await shareClockReader.ReadTimezoneAsync(cancellationToken)
            : await therapySettings.GetTimezoneAsync(ct: cancellationToken);
        return Ok(new PatientTimezoneResponse { Timezone = timezone });
    }
}
