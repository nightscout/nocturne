using System.Reflection;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Base class providing default implementation for sanitization methods using reflection.
/// Properties marked with <see cref="SanitizableAttribute"/> are automatically discovered
/// and included in HTML sanitization passes.
/// </summary>
/// <seealso cref="IProcessableDocument"/>
/// <seealso cref="SanitizableAttribute"/>
public abstract class ProcessableDocumentBase : IProcessableDocument
{
    /// <inheritdoc />
    /// <remarks>
    /// Ignored here because System.Text.Json serializes this declaration as its own property next
    /// to every override's renamed <c>_id</c>, shipping a second, unconverted copy of the id.
    /// Readers that must keep an uploader's <c>id</c> also drop it: see
    /// <see cref="Serializers.UploaderIdJsonModifier"/>.
    /// </remarks>
    [JsonIgnore]
    public abstract string? Id { get; set; }

    /// <summary>
    /// The <c>identifier</c> an upstream Nightscout returned the document with. Nightscout
    /// write-back sends a record's own key there, because Nightscout 15.0.7 and later replace a
    /// <c>_id</c> that is not an ObjectId (and, for a treatment carrying an identifier, any
    /// <c>_id</c>) with one it mints, so the pulled-back copy is only recognisable by it. Set by the
    /// Nightscout connector's reads alone; never on the wire.
    /// </summary>
    [JsonIgnore]
    public string? UpstreamIdentifier { get; set; }

    /// <inheritdoc />
    public abstract string? CreatedAt { get; set; }

    /// <inheritdoc />
    public abstract long Mills { get; set; }

    /// <inheritdoc />
    public abstract int? UtcOffset { get; set; }

    /// <inheritdoc />
    public virtual Dictionary<string, string?> GetSanitizableFields()
    {
        var fields = new Dictionary<string, string?>();
        var sanitizableProperties = GetSanitizableProperties();

        foreach (var property in sanitizableProperties)
        {
            var value = property.GetValue(this) as string;
            if (!string.IsNullOrEmpty(value))
            {
                fields.Add(property.Name, value);
            }
        }

        return fields;
    }

    /// <inheritdoc />
    public virtual void SetSanitizedField(string fieldName, string? sanitizedValue)
    {
        var sanitizableProperties = GetSanitizableProperties();
        var property = sanitizableProperties.FirstOrDefault(p => p.Name == fieldName);

        property?.SetValue(this, sanitizedValue);
    }

    /// <summary>
    /// Gets the properties that should be sanitized. Properties marked with [Sanitizable] attribute will be included.
    /// If no properties have the attribute, falls back to all public string properties.
    /// </summary>
    /// <returns>Collection of properties that should be sanitized</returns>
    protected virtual IEnumerable<PropertyInfo> GetSanitizableProperties()
    {
        var allStringProperties = GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite)
            .ToList();

        var attributeMarkedProperties = allStringProperties
            .Where(p => p.GetCustomAttribute<SanitizableAttribute>() != null)
            .ToList();

        // If any properties have the attribute, use only those. Otherwise, use all string properties.
        return attributeMarkedProperties.Any() ? attributeMarkedProperties : allStringProperties;
    }
}
