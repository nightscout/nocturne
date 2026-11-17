using System.Text.Json.Serialization.Metadata;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Nightscout.Services.WriteBack;

/// <summary>
/// The <see cref="UpstreamIdentityJson.Options"/> modifier that leaves <c>preBolus</c> out of a
/// treatment written back to Nightscout.
/// </summary>
/// <remarks>
/// Nightscout's v1 treatment writes (<c>lib/server/treatments.js</c>) never store <c>preBolus</c>
/// beside <c>carbs</c>: <c>prepareData</c> deletes the carbs of any document with a non-zero
/// <c>preBolus</c>, and a POST then upserts a second document, <c>{created_at + preBolus minutes,
/// eventType, carbs, notes}</c>, with no <c>identifier</c> and a minted <c>_id</c>, carbs or not. The
/// connector's pull cannot tell that document from another uploader's and stores its carbs as a
/// second intake, and a PUT drops the carbs from upstream altogether. Write-back splits a meal itself,
/// under ids the pull resolves (<see cref="PreBolusSplit.ForWriteBack"/>), and sends the carbs of any
/// other treatment at its own time, as Nocturne holds them.
/// </remarks>
internal static class TreatmentWriteBackJson
{
    private const string PreBolusName = "preBolus";

    public static void OmitPreBolus(JsonTypeInfo info)
    {
        if (!typeof(Treatment).IsAssignableFrom(info.Type))
            return;

        foreach (var property in info.Properties)
        {
            if (property.Name == PreBolusName)
                property.ShouldSerialize = static (_, _) => false;
        }
    }
}
