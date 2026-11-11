using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Nocturne.API.OpenApi;

/// <summary>
/// Publishes numbers as numbers. The schema generator reads the web JSON defaults, whose
/// <see cref="JsonNumberHandling.AllowReadingFromString"/> turns every integer and number into
/// <c>["integer", "string"]</c> with a digits pattern, and Scalar then shows each one as a string,
/// in parameters and in example responses.
/// </summary>
/// <remarks>
/// Reading a quoted number by default is a tolerance of the request reader, not the contract:
/// responses always write numbers, and query and route values are strings on the wire whatever the
/// type. A property or type that opts into <see cref="JsonNumberHandling.AllowReadingFromString"/>
/// itself (models legacy uploaders post quoted numbers to) keeps the string form.
/// </remarks>
public sealed class NumericSchemaTransformer : IOpenApiSchemaTransformer
{
    private const JsonSchemaType Numeric = JsonSchemaType.Integer | JsonSchemaType.Number;

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (!AcceptsQuotedNumbers(context.JsonPropertyInfo))
            Collapse(schema);
        return Task.CompletedTask;
    }

    internal static bool AcceptsQuotedNumbers(JsonPropertyInfo? property)
    {
        if (property is null)
            return false;

        var handling = property.NumberHandling
            ?? property.DeclaringType.GetCustomAttribute<JsonNumberHandlingAttribute>(inherit: true)?.Handling;
        return handling is { } h && h.HasFlag(JsonNumberHandling.AllowReadingFromString);
    }

    internal static void Collapse(OpenApiSchema schema)
    {
        if (schema.Type is not { } type
            || (type & Numeric) == 0
            || (type & JsonSchemaType.String) == 0)
            return;

        schema.Type = type & ~JsonSchemaType.String;
        schema.Pattern = null;
    }
}
