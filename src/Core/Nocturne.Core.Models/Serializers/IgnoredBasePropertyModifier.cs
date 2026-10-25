using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Nocturne.Core.Models.Serializers;

/// <summary>
/// Removes the ignored base <c>Id</c> and <c>CreatedAt</c> declarations from a document's
/// properties, so an uploader's lowercase <c>id</c> and camelCase <c>createdAt</c> are kept when reading.
/// </summary>
/// <remarks>
/// A document overrides <see cref="ProcessableDocumentBase.Id"/> and
/// <see cref="ProcessableDocumentBase.CreatedAt"/> under <c>[JsonPropertyName("_id")]</c> and
/// <c>[JsonPropertyName("created_at")]</c>. System.Text.Json still lists each abstract base declaration
/// as a second property, named <c>Id</c> and <c>CreatedAt</c>, and <see cref="Treatment"/> ignores its own
/// <c>CreatedAt</c> override the same way.
/// They are <c>[JsonIgnore]</c>d so they are never written beside the renamed members, but an ignored
/// property keeps its name: under case-insensitive matching it swallows an uploader's <c>id</c> or
/// <c>createdAt</c>, which is then lost. Removing them sends both to the extension data, where
/// <see cref="TreatmentClientId"/> reads <c>id</c> as the plain field it is.
/// </remarks>
public static class IgnoredBasePropertyModifier
{
    private static readonly string[] IgnoredMembers =
    [
        nameof(ProcessableDocumentBase.Id),
        nameof(ProcessableDocumentBase.CreatedAt),
    ];

    public static void RemoveIgnoredBaseProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || !typeof(ProcessableDocumentBase).IsAssignableFrom(typeInfo.Type))
            return;

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (typeInfo.Properties[i].AttributeProvider is MemberInfo member
                && IgnoredMembers.Contains(member.Name)
                && member.GetCustomAttribute<JsonIgnoreAttribute>(inherit: false) is { Condition: JsonIgnoreCondition.Always })
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }
}
