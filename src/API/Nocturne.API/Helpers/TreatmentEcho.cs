using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Helpers;

/// <summary>
/// The reply to a bulk treatment write: one element per submitted treatment, in submission order,
/// because Loop and other NightscoutKit uploaders pair the reply with the request by position.
/// </summary>
public static class TreatmentEcho
{
    /// <summary>
    /// The written treatments with each refused one put back at its position, as submitted and
    /// carrying an <c>error</c> member that says why it was not stored.
    /// </summary>
    public static Treatment[] InSubmittedOrder(BulkWrite<Treatment> written)
    {
        if (written.Rejected.Count == 0)
            return [.. written];

        var echo = new List<Treatment>(written);
        foreach (var refused in written.Rejected.OrderBy(r => r.Index))
        {
            refused.Record.AdditionalProperties ??= [];
            refused.Record.AdditionalProperties["error"] = refused.Reason;
            echo.Insert(refused.Index, refused.Record);
        }

        return [.. echo];
    }
}
