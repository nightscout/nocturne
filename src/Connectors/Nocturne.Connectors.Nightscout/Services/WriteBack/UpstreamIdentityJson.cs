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
/// So <c>_id</c> goes out as the 24-hex ObjectId every Nocturne read serves
/// (<see cref="MongoObjectId.Coerce"/>), and <c>identifier</c> carries the record's own key
/// verbatim: a treatment's <see cref="Treatment.LegacyId"/>, else the id the record carries, which
/// is its legacy id or, with none, its uuid. The pull-back resolves <c>identifier</c> first and
/// <c>_id</c> after (<c>DecomposerBase.PointAtStoredRecordsAsync</c>).
/// </para>
/// <para>
/// Two exceptions. A treatment whose key is itself an ObjectId goes out under that key as
/// <c>_id</c> with no <c>identifier</c>: the upsert then matches the copy on its ObjectId, which the
/// <c>{_id: identifier}</c> arm of its identifier match never does, since that arm compares a
/// string. An entry whose id is not an ObjectId goes out with no <c>_id</c>, since any <c>_id</c>
/// other than the stored reading's breaks the entries upsert.
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
        identifier.Get = KeyOf;

        var id = info.Properties.FirstOrDefault(p => p.Name == IdName);
        if (id?.Get is not { } served)
            return;

        if (typeof(Treatment).IsAssignableFrom(info.Type))
        {
            // An ObjectId key keeps its _id upstream, and an identifier would stop the upsert matching
            // on it: a copy written without one (as before identifiers were sent) would be duplicated.
            identifier.ShouldSerialize = (document, _) => !MongoObjectId.IsObjectId(KeyOf(document));
            id.Get = document => KeyOf(document) is { } key && MongoObjectId.IsObjectId(key) ? key : served(document);
        }
        else if (typeof(Entry).IsAssignableFrom(info.Type))
        {
            // Entries upsert by sysTime and type with $set: an _id that differs from the stored
            // reading's is an immutable-field error that aborts the whole ordered batch.
            id.ShouldSerialize = (document, _) => MongoObjectId.IsObjectId(((Entry)document).Id);
        }
    }

    private static string? KeyOf(object document) => document is Treatment { LegacyId: { } legacyId }
        ? legacyId
        : ((ProcessableDocumentBase)document).Id;

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
