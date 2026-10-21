using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Nightscout.Services.WriteBack;

/// <summary>
/// Serializer options for a treatment written back to Nightscout, which leave out <c>preBolus</c>.
/// </summary>
/// <remarks>
/// Nightscout's v1 treatment writes (<c>lib/server/treatments.js</c>) never store <c>preBolus</c>
/// beside <c>carbs</c>: <c>prepareData</c> deletes the carbs of any document with a non-zero
/// <c>preBolus</c>, and a POST then upserts a second document, <c>{created_at + preBolus minutes,
/// eventType, carbs, notes}</c>, with no <c>identifier</c> and a minted <c>_id</c>, carbs or not. The
/// connector's pull cannot tell that document from another uploader's and stores its carbs as a
/// second intake, and a PUT drops the carbs from upstream altogether. Nocturne stores the carbs of a
/// written-back treatment at its own time, so the one document it sends is what it holds.
/// </remarks>
internal static class TreatmentWriteBackJson
{
    private const string PreBolusName = "preBolus";

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitPreBolus } },
    };

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
