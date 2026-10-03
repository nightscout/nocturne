using System.Text.Json;
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
/// A treatment goes out as it always has, so that the copies earlier write-backs left upstream are
/// the ones its upserts land on: <c>_id</c> and <c>identifier</c> both carry
/// <see cref="TreatmentWireKey"/>, the 24-hex coercion of its key. On 15.0.7 and later the upsert
/// then matches the copy by that <c>identifier</c>. <see cref="IdOnlyOptions"/> leaves the
/// <c>identifier</c> out, for an edit of a treatment upstream holds under its <c>_id</c> alone
/// (<see cref="NightscoutTreatmentWriteBackSink"/>).
/// </para>
/// </remarks>
internal static class UpstreamIdentityJson
{
    private const string IdentifierName = "identifier";
    private const string IdName = "_id";

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { info => WriteIdentifier(info, withTreatmentIdentifier: true) } },
    };

    /// <summary><see cref="Options"/>, with no <c>identifier</c> on a treatment.</summary>
    public static JsonSerializerOptions IdOnlyOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { info => WriteIdentifier(info, withTreatmentIdentifier: false) } },
    };

    /// <summary>
    /// The id a treatment goes upstream under: <see cref="MongoObjectId.Coerce"/> of its
    /// <see cref="Treatment.LegacyId"/>, else of its id. A v1 create carries the legacy id as its id
    /// and a v1 edit carries it apart, so both write the same copy.
    /// </summary>
    public static string? TreatmentWireKey(Treatment treatment) => MongoObjectId.Coerce(treatment.LegacyId ?? treatment.Id);

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

    private static void WriteIdentifier(JsonTypeInfo info, bool withTreatmentIdentifier)
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
            if (!withTreatmentIdentifier)
                identifier.ShouldSerialize = (_, _) => false;
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
