using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Nocturne.API.OpenApi;

/// <summary>
/// Publishes numbers as numbers. The schema generator reads the web JSON defaults, whose
/// <see cref="System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString"/> turns
/// every integer and number into <c>["integer", "string"]</c> with a digits pattern, and Scalar
/// then shows each one as a string, in parameters and in example responses.
/// </summary>
/// <remarks>
/// Reading a quoted number is a tolerance of the request reader, not the contract: responses
/// always write numbers, and query and route values are strings on the wire whatever the type.
/// </remarks>
public sealed class NumericSchemaTransformer : IOpenApiSchemaTransformer
{
    private const JsonSchemaType Numeric = JsonSchemaType.Integer | JsonSchemaType.Number;

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        Collapse(schema);
        return Task.CompletedTask;
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
