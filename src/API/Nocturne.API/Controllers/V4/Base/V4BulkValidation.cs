using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Models.Requests.V4;

namespace Nocturne.API.Controllers.V4.Base;

/// <summary>
/// The checks a V4 bulk create-or-update endpoint runs over its payload before it maps or
/// persists anything.
/// </summary>
public static class V4BulkValidation
{
    /// <summary>
    /// Maximum records one bulk create-or-update request may carry.
    /// </summary>
    /// <remarks>
    /// Flat-record bulks share this ceiling; a shape whose items nest collections carries its own
    /// lower one (<c>SleepController.CreateSessionsBulk</c>).
    /// </remarks>
    public const int MaxItems = 1000;

    /// <summary>
    /// Rejects a payload that is empty, longer than <see cref="MaxItems"/>, carries an unset
    /// timestamp, or supplies a <c>SyncIdentifier</c> with no <c>DataSource</c> — which would
    /// leave the row outside the (DataSource, SyncIdentifier) key the upsert matches on, so a
    /// re-upload of the same record would insert a duplicate instead of updating it. Then runs
    /// the registered <see cref="IValidator{T}"/> over each item.
    /// </summary>
    /// <remarks>
    /// FluentValidation's auto-validation filter does not descend into a root-array body, so the
    /// per-item rules only run for a bulk payload because this chokepoint invokes them. The
    /// validator is optional: a request type with none registered is not newly rejected.
    /// </remarks>
    /// <param name="controller">The controller answering the request.</param>
    /// <param name="requests">The payload as bound from the body.</param>
    /// <param name="subject">The payload's name, as it opens the empty-payload message ("Bolus").</param>
    /// <param name="singular">One item ("bolus").</param>
    /// <param name="plural">Many items ("boluses").</param>
    /// <param name="ct">Cancels the per-item validation.</param>
    /// <returns>The error response to return, or <c>null</c> when the payload is usable.</returns>
    public static async Task<ObjectResult?> ValidateBulkAsync<TRequest>(
        this ControllerBase controller,
        TRequest[]? requests,
        string subject,
        string singular,
        string plural,
        CancellationToken ct = default)
        where TRequest : IBulkUpsertRequest
    {
        if (requests is not { Length: > 0 })
            return controller.Problem(detail: $"{subject} data is required", statusCode: 400, title: "Bad Request");

        if (requests.Length > MaxItems)
            return controller.Problem(detail: $"Bulk operations are limited to {MaxItems} {plural} per request", statusCode: 400, title: "Bad Request");

        if (requests.Any(r => r.Timestamp == default))
            return controller.Problem(detail: $"Timestamp must be set on every {singular}", statusCode: 400, title: "Bad Request");

        if (requests.Any(r => !string.IsNullOrEmpty(r.SyncIdentifier) && string.IsNullOrEmpty(r.DataSource)))
            return controller.Problem(detail: "DataSource is required when SyncIdentifier is supplied", statusCode: 400, title: "Bad Request");

        if (controller.HttpContext?.RequestServices?.GetService(typeof(IValidator<TRequest>)) is not IValidator<TRequest> validator)
            return null;

        for (var index = 0; index < requests.Length; index++)
        {
            var result = await validator.ValidateAsync(requests[index], ct);
            if (result.IsValid)
                continue;

            var failure = result.Errors[0];
            return controller.Problem(
                detail: $"{subject} at index {index} is invalid: {failure.PropertyName}: {failure.ErrorMessage}",
                statusCode: 400,
                title: "Bad Request");
        }

        return null;
    }
}
