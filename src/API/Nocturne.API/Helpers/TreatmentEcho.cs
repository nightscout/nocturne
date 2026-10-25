using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Helpers;

/// <summary>
/// The reply to a bulk treatment write: one element per submitted treatment, in submission order,
/// because Loop and other NightscoutKit uploaders pair the reply with the request by position. A
/// meal a v1 POST splits (<see cref="Nocturne.API.Services.Treatments.PreBolusSplit"/>) echoes two
/// elements in its place, the bolus and then its carbs, as Nightscout's POST does.
/// </summary>
public static class TreatmentEcho
{
    /// <summary>
    /// The member a refused treatment's echo carries, saying why it was not stored. Namespaced so it
    /// cannot be mistaken for a field a client sent.
    /// </summary>
    public const string RefusalMember = "nocturneError";

    /// <summary>
    /// The written treatments with each refused one put back at its position, as submitted and
    /// carrying <see cref="RefusalMember"/>.
    /// </summary>
    /// <remarks>
    /// NIGHTSCOUT-COMPAT: Nightscout stores any document, so v1 uploaders treat every element of a
    /// 2xx reply as stored; NightscoutKit also pairs each echoed <c>_id</c> with its request. A
    /// refused treatment is therefore still echoed, keeping its <c>_id</c>, and is kept in the
    /// tenant's rejected uploads, where the owner sees what the uploader believes it saved.
    /// </remarks>
    public static Treatment[] InSubmittedOrder(BulkWrite<Treatment> written)
    {
        if (written.Rejected.Count == 0)
            return [.. written];

        var echo = new List<Treatment>(written);
        foreach (var refused in written.Rejected.OrderBy(r => r.Index))
            echo.Insert(refused.Index, Refused(refused.Record, refused.Reason));

        return [.. echo];
    }

    /// <summary><paramref name="treatment"/> as submitted, carrying <see cref="RefusalMember"/>.</summary>
    public static Treatment Refused(Treatment treatment, string reason)
    {
        treatment.AdditionalProperties ??= [];
        treatment.AdditionalProperties[RefusalMember] = reason;
        return treatment;
    }
}
