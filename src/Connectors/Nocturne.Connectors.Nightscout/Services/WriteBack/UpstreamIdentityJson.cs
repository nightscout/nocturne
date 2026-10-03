using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Serializers;

namespace Nocturne.Connectors.Nightscout.Services.WriteBack;

/// <summary>
/// Serializer options that keep a record's identity across a write-back round trip, in both
/// directions: <see cref="Options"/> for what write-back sends, <see cref="ReadOptions"/> for what
/// the connector pulls back.
/// </summary>
/// <remarks>
/// <para>
/// Nightscout does not reliably keep the <c>_id</c> it is sent. From 15.0.7 it replaces any
/// <c>_id</c> that is not a 24-hex ObjectId with one it mints (moving the value to
/// <c>identifier</c> when that is empty), its devicestatus endpoint refuses such an <c>_id</c>
/// outright, and a treatment upsert that carries an <c>identifier</c> matches on it and drops the
/// <c>_id</c> altogether. Up to 15.0.6 a string <c>_id</c> is stored as sent but breaks its own
/// ObjectId lookups and AAPS's <c>isObjectId()</c>. Every version stores <c>identifier</c> as sent.
/// </para>
/// <para>
/// So <c>identifier</c> carries the key the upstream copy is found by, and <c>_id</c> goes out
/// only where the upstream keeps it:
/// <list type="bullet">
/// <item>a device status: <c>_id</c> is <see cref="MongoObjectId.Coerce"/> of its id, the ObjectId
/// every Nocturne read serves, and <c>identifier</c> its id verbatim;</item>
/// <item>an entry: <c>identifier</c> is its id verbatim, and <c>_id</c> goes out only when that id
/// is an ObjectId, since any <c>_id</c> other than the stored reading's breaks the entries
/// upsert;</item>
/// <item>a treatment: both carry <see cref="TreatmentWireKey"/>, below.</item>
/// </list>
/// The pull-back resolves <c>identifier</c> first and <c>_id</c> after
/// (<c>DecomposerBase.PlanStoredIdentitiesAsync</c>).
/// </para>
/// <para>
/// A treatment is created upstream with <c>_id</c> and <c>identifier</c> both carrying
/// <see cref="TreatmentWireKey"/>, the 24-hex coercion of its key. On 15.0.7 and later the upsert
/// then matches the copy by that <c>identifier</c>. Earlier releases left copies under other forms
/// (<see cref="TreatmentWireForms"/>), and an edit is written onto whichever copy the upstream holds
/// (<see cref="NightscoutTreatmentWriteBackSink"/>, through <see cref="TreatmentPayload"/>).
/// </para>
/// </remarks>
internal static class UpstreamIdentityJson
{
    private const string IdentifierName = "identifier";
    private const string IdName = "_id";

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { WriteIdentifier } },
    };

    /// <summary>
    /// The id a treatment goes upstream under: <see cref="MongoObjectId.Coerce"/> of its
    /// <see cref="Treatment.LegacyId"/>, else of its id. A v1 create carries the legacy id as its id
    /// and a v1 edit carries it apart, so both write the same copy.
    /// </summary>
    public static string? TreatmentWireKey(Treatment treatment) => MongoObjectId.Coerce(treatment.LegacyId ?? treatment.Id);

    /// <summary>
    /// Every <c>identifier</c> a released or merged write-back may have left a treatment's copy under,
    /// most current first, without repeats:
    /// <list type="number">
    /// <item><see cref="TreatmentWireKey"/>, what this release creates under;</item>
    /// <item>the 24-hex prefix of its record's own uuid (<see cref="Treatment.RecordId"/>): temp
    /// basals from v0.2.4 to v0.2.7, whose legacy id was not an ObjectId
    /// (<c>TempBasalToTreatmentMapper</c> served the uuid), and every create on main after #1960,
    /// which answered with the uuid;</item>
    /// <item>its key as it is, which v0.0.1 to v0.2.3 sent with no coercion: the legacy id, else
    /// the record's uuid.</item>
    /// </list>
    /// These are the forms the connector's pull resolves back to the record
    /// (<c>DecomposerBase.PlanStoredIdentitiesAsync</c>).
    /// </summary>
    public static IReadOnlyList<string> TreatmentWireForms(Treatment treatment)
    {
        string?[] forms =
        [
            TreatmentWireKey(treatment),
            treatment.RecordId is { } recordId ? MongoObjectId.FromGuid(recordId) : null,
            treatment.LegacyId ?? treatment.RecordId?.ToString() ?? treatment.Id,
        ];
        return forms.OfType<string>().Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// <paramref name="treatment"/> as <see cref="Options"/> writes it, with its <c>_id</c> and
    /// <c>identifier</c> replaced by <paramref name="id"/> and <paramref name="identifier"/>, each left
    /// out when null.
    /// </summary>
    public static JsonObject TreatmentPayload(Treatment treatment, string? id, string? identifier)
    {
        var payload = JsonSerializer.SerializeToNode(treatment, Options)!.AsObject();
        payload.Remove(IdName);
        payload.Remove(IdentifierName);
        if (id is not null)
            payload[IdName] = id;
        if (identifier is not null)
            payload[IdentifierName] = identifier;
        return payload;
    }

    /// <summary>
    /// <see cref="JsonDefaults.CaseInsensitive"/>, plus an upstream document's <c>identifier</c>
    /// read into <see cref="ProcessableDocumentBase.UpstreamIdentifier"/>, where the models would
    /// otherwise drop it as a read-only alias of their id.
    /// </summary>
    public static JsonSerializerOptions ReadOptions { get; } = new(JsonDefaults.CaseInsensitive)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { UploaderIdJsonModifier.RemoveBaseIdProperty, ReadIdentifier },
        },
    };

    private static void WriteIdentifier(JsonTypeInfo info)
    {
        if (!typeof(ProcessableDocumentBase).IsAssignableFrom(info.Type))
            return;

        var identifier = IdentifierProperty(info);
        identifier.CustomConverter = null;
        identifier.Get = document => ((ProcessableDocumentBase)document).Id;

        var id = info.Properties.FirstOrDefault(p => p.Name == IdName);
        if (typeof(Treatment).IsAssignableFrom(info.Type))
        {
            identifier.Get = document => TreatmentWireKey((Treatment)document);
            if (id is not null)
                id.Get = document => TreatmentWireKey((Treatment)document);
        }
        else if (id is not null && typeof(Entry).IsAssignableFrom(info.Type))
        {
            // Entries upsert by sysTime and type with $set: an _id that differs from the stored
            // reading's is an immutable-field error that aborts the whole ordered batch.
            id.ShouldSerialize = (document, _) => MongoObjectId.IsObjectId(((Entry)document).Id);
        }
    }

    private static void ReadIdentifier(JsonTypeInfo info)
    {
        if (!typeof(ProcessableDocumentBase).IsAssignableFrom(info.Type))
            return;

        var identifier = IdentifierProperty(info);
        identifier.CustomConverter = null;
        identifier.Get = document => ((ProcessableDocumentBase)document).UpstreamIdentifier;
        identifier.Set = (document, value) => ((ProcessableDocumentBase)document).UpstreamIdentifier = value as string;
    }

    /// <summary>The type's <c>identifier</c> property, added where the model declares none.</summary>
    private static JsonPropertyInfo IdentifierProperty(JsonTypeInfo info)
    {
        var existing = info.Properties.FirstOrDefault(p => p.Name == IdentifierName);
        if (existing is not null && existing.PropertyType == typeof(string))
            return existing;
        if (existing is not null)
            info.Properties.Remove(existing);

        var added = info.CreateJsonPropertyInfo(typeof(string), IdentifierName);
        info.Properties.Add(added);
        return added;
    }
}
