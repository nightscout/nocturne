using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// Controller for heart rate data from diabetes apps and wearables.
/// </summary>
/// <remarks>
/// Heart rate readings are stored as time-series observations. All operations delegate to
/// <see cref="IHeartRateService"/>. Callers must hold the <c>read:health</c>
/// or <c>write:health</c> scope as appropriate.
/// </remarks>
/// <seealso cref="IHeartRateService"/>
[ApiController]
[Tags("Health")]
[Route("api/v4/[controller]")]
[Produces("application/json")]
public class HeartRateController(IHeartRateService heartRateService)
    : HealthSeriesControllerBase<HeartRate, UpsertHeartRateRequest>
{
    protected override string RecordTypeName => "Heart rate";

    protected override Task<IEnumerable<HeartRate>> ReadPageAsync(int count, int skip, CancellationToken ct) =>
        heartRateService.GetHeartRatesAsync(count, skip, ct);

    protected override Task<IEnumerable<HeartRate>> ReadRangeAsync(
        DateTime from, DateTime to, int count, int skip, CancellationToken ct) =>
        heartRateService.GetHeartRatesByDateRangeAsync(from, to, count, skip, ct);

    protected override Task<HeartRate?> ReadAsync(string id, CancellationToken ct) =>
        heartRateService.GetHeartRateByIdAsync(id, ct);

    protected override Task<IEnumerable<HeartRate>> WriteManyAsync(IReadOnlyList<HeartRate> models, CancellationToken ct) =>
        heartRateService.CreateHeartRatesAsync(models, ct);

    protected override Task<HeartRate?> WriteAsync(string id, HeartRate model, CancellationToken ct) =>
        heartRateService.UpdateHeartRateAsync(id, model, ct);

    protected override Task<bool> EraseAsync(string id, CancellationToken ct) =>
        heartRateService.DeleteHeartRateAsync(id, ct);

    protected override HeartRate ToModel(UpsertHeartRateRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Bpm = request.Bpm,
        Accuracy = request.Accuracy,
        Device = request.Device,
        EnteredBy = request.App,
        DataSource = request.DataSource,
        SyncIdentifier = request.SyncIdentifier,
    };

    /// <summary>
    /// Get heart rate records with optional pagination and date filtering
    /// </summary>
    /// <param name="count">Maximum number of records to return (default: 10, or up to the ceiling when from/to are specified)</param>
    /// <param name="skip">Number of records to skip for pagination (default: 0)</param>
    /// <param name="from">Start of date range (inclusive).</param>
    /// <param name="to">End of date range (exclusive).</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of heart rate records</returns>
    /// <remarks>
    /// A date range without a <paramref name="count"/> reads up to
    /// <see cref="V4ReadLimits.MaxPageSize"/> records rather than the whole range, so a wide range
    /// cannot load the table into memory. Page through the rest with <paramref name="skip"/>.
    /// </remarks>
    [HttpGet]
    [RemoteQuery]
    [RequireScope(Scope.HeartRateRead)]
    [ProducesResponseType(typeof(IEnumerable<HeartRate>), 200)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult<IEnumerable<HeartRate>>> GetHeartRates(
        [FromQuery] int? count = null,
        [FromQuery] int skip = 0,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken cancellationToken = default
    ) => ListResponseAsync(count, skip, from, to, cancellationToken);

    /// <summary>
    /// Get a specific heart rate record by ID
    /// </summary>
    /// <param name="id">Record ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet("{id}")]
    [RemoteQuery]
    [RequireScope(Scope.HeartRateRead)]
    [ProducesResponseType(typeof(HeartRate), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult<HeartRate>> GetHeartRate(
        string id,
        CancellationToken cancellationToken = default
    ) => GetResponseAsync(id, cancellationToken);

    /// <summary>
    /// Create one or more heart rate records
    /// </summary>
    [HttpPost]
    [RequireScope(Scope.HeartRateReadWrite)]
    [ProducesResponseType(typeof(IEnumerable<HeartRate>), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult<IEnumerable<HeartRate>>> CreateHeartRates(
        [FromBody] UpsertHeartRateRequest[] requests,
        CancellationToken cancellationToken = default
    ) => CreateResponseAsync(requests, cancellationToken);

    /// <summary>
    /// Update an existing heart rate record
    /// </summary>
    [HttpPut("{id}")]
    [RequireScope(Scope.HeartRateReadWrite)]
    [ProducesResponseType(typeof(HeartRate), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult<HeartRate>> UpdateHeartRate(
        string id,
        [FromBody] UpsertHeartRateRequest request,
        CancellationToken cancellationToken = default
    ) => UpdateResponseAsync(id, ToModel(request), cancellationToken);

    /// <summary>
    /// Delete a heart rate record by ID
    /// </summary>
    [HttpDelete("{id}")]
    [RequireScope(Scope.HeartRateReadWrite)]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult> DeleteHeartRate(
        string id,
        CancellationToken cancellationToken = default
    ) => DeleteResponseAsync(id, cancellationToken);
}
