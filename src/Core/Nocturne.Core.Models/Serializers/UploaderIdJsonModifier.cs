using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Nocturne.Core.Models.Serializers;

/// <summary>
/// Keeps an uploader's lowercase <c>id</c> out of a document's <c>_id</c> identity when reading.
/// </summary>
/// <remarks>
/// A document overrides <see cref="ProcessableDocumentBase.Id"/> under <c>[JsonPropertyName("_id")]</c>.
/// System.Text.Json still lists the abstract base declaration as a second property named <c>Id</c>.
/// It is <c>[JsonIgnore]</c>d so it is never written beside <c>_id</c>, but an ignored property
/// keeps its name: under case-insensitive matching it swallows an uploader's <c>id</c>, which is
/// then lost. Removing it sends <c>id</c> to the extension data, where <see cref="TreatmentClientId"/>
/// reads it as the plain field it is.
/// </remarks>
public static class UploaderIdJsonModifier
{
    /// <summary>Case-insensitive options for request bodies deserialized by hand.</summary>
    public static JsonSerializerOptions CaseInsensitiveReadOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RemoveBaseIdProperty } },
    };

    public static void RemoveBaseIdProperty(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || !typeof(ProcessableDocumentBase).IsAssignableFrom(typeInfo.Type))
            return;

        var baseId = typeInfo.Properties.FirstOrDefault(p =>
            p.AttributeProvider is MemberInfo { Name: nameof(ProcessableDocumentBase.Id) } member
            && member.DeclaringType == typeof(ProcessableDocumentBase));

        if (baseId is not null)
            typeInfo.Properties.Remove(baseId);
    }
}
