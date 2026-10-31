using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace Nocturne.API.Reports;

/// <summary>
/// A dataset's parameters as JSON Schema, which the web app's report builder draws as a form:
/// System.Text.Json's schema of the <see cref="ReportDatasetParameters"/> subtype, with the
/// attributes the validator enforces written into it.
/// </summary>
/// <remarks>
/// Added to the exporter's output: <c>title</c> (<see cref="DisplayNameAttribute"/>, else the name
/// in words), <c>description</c> (<see cref="DescriptionAttribute"/>), <c>minimum</c>/<c>maximum</c>
/// (<see cref="RangeAttribute"/>), <c>maxLength</c> (<see cref="StringLengthAttribute"/>),
/// <c>minItems</c>/<c>maxItems</c> (<see cref="LengthAttribute"/>), <c>enum</c>
/// (<see cref="AllowedValuesAttribute"/>), <c>pattern</c>, the top-level <c>default</c>s a new
/// parameters object holds, <c>x-enumLabels</c> beside every <c>enum</c>, and
/// <c>x-input</c> (<see cref="ReportInputAttribute"/>) for a value picked from the tenant's own
/// records. A nullable type is written as its non-null type: a parameter left out is null.
/// </remarks>
internal static partial class ReportParametersSchema
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = Transform,
    };

    public static JsonElement For(Type parametersType)
    {
        var schema = Options.GetJsonSchemaAsNode(parametersType, ExporterOptions);
        Defaults(schema, parametersType);
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>
    /// The value each top-level parameter holds in a new parameters object, as its <c>default</c>, so
    /// the form shows it filled in. Read from an instance of the concrete type, since a parameter
    /// declared on an abstract base is given its default there.
    /// </summary>
    private static void Defaults(JsonNode schema, Type parametersType)
    {
        if (schema["properties"] is not JsonObject properties || parametersType.GetConstructor(Type.EmptyTypes) is null)
            return;
        var instance = JsonSerializer.SerializeToNode(Activator.CreateInstance(parametersType), parametersType, Options)!.AsObject();
        foreach (var (name, property) in properties)
        {
            if (property is JsonObject node && instance[name] is JsonValue value)
                node["default"] = value.DeepClone();
        }
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode node)
    {
        if (node is not JsonObject schema)
            return node;

        DropNull(schema);

        var type = Nullable.GetUnderlyingType(context.TypeInfo.Type) ?? context.TypeInfo.Type;
        if (type.IsEnum && schema["enum"] is JsonArray values)
        {
            schema["type"] = "string";
            schema["x-enumLabels"] = new JsonArray([.. values.Select(v => (JsonNode?)JsonValue.Create(EnumLabel(type, v?.GetValue<string>())))]);
        }

        if (context.PropertyInfo?.AttributeProvider is not PropertyInfo property)
            return schema;

        schema["title"] = property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? Words(property.Name);
        if (property.GetCustomAttribute<DescriptionAttribute>() is { } description)
            schema["description"] = description.Description;
        if (property.GetCustomAttribute<RangeAttribute>() is { } range)
        {
            schema["minimum"] = JsonValue.Create(Convert.ToDouble(range.Minimum, System.Globalization.CultureInfo.InvariantCulture));
            schema["maximum"] = JsonValue.Create(Convert.ToDouble(range.Maximum, System.Globalization.CultureInfo.InvariantCulture));
        }
        if (property.GetCustomAttribute<StringLengthAttribute>() is { } length)
        {
            schema["maxLength"] = length.MaximumLength;
            if (length.MinimumLength > 0)
                schema["minLength"] = length.MinimumLength;
        }
        if (property.GetCustomAttribute<LengthAttribute>() is { } items)
        {
            schema["minItems"] = items.MinimumLength;
            schema["maxItems"] = items.MaximumLength;
        }
        if (property.GetCustomAttribute<AllowedValuesAttribute>() is { } allowed)
        {
            schema["enum"] = new JsonArray([.. allowed.Values.Select(v => JsonSerializer.SerializeToNode(v, Options))]);
            schema["x-enumLabels"] = new JsonArray([.. allowed.Values.Select(v => (JsonNode?)JsonValue.Create(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))]);
        }
        if (property.GetCustomAttribute<RegularExpressionAttribute>() is { } pattern)
            schema["pattern"] = pattern.Pattern;
        if (property.GetCustomAttribute<ReportInputAttribute>() is { } input)
            schema["x-input"] = input.Kind;

        return schema;
    }

    /// <summary>A nullable type's schema as its non-null type: <c>["integer", "null"]</c> becomes <c>"integer"</c>.</summary>
    private static void DropNull(JsonObject schema)
    {
        if (schema["type"] is JsonArray types && types.Count(t => t?.GetValue<string>() != "null") == 1)
            schema["type"] = types.First(t => t?.GetValue<string>() != "null")!.GetValue<string>();
        if (schema["enum"] is JsonArray values && values.Any(v => v is null))
            schema["enum"] = new JsonArray([.. values.Where(v => v is not null).Select(v => v!.DeepClone())]);
    }

    /// <summary>An enum value's words: the member's <see cref="DescriptionAttribute"/>, else its name in words.</summary>
    private static string EnumLabel(Type type, string? wire)
    {
        var member = type.GetFields(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(f =>
            (f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? f.Name) == wire);
        return member?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? Words(member?.Name ?? wire ?? string.Empty);
    }

    /// <summary><c>ExcludeOpenLoop</c> as "Exclude open loop".</summary>
    internal static string Words(string name)
    {
        var spaced = WordBoundary().Replace(name, " $1").Trim().ToLowerInvariant();
        return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();
}

/// <summary>
/// A parameter whose values are the tenant's own records, which the report builder picks with a
/// search rather than a list (<c>foods</c>, <c>food-category</c>), or a number the builder shows in
/// another form (<c>minute-of-day</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class ReportInputAttribute(string kind) : Attribute
{
    public const string Foods = "foods";
    public const string FoodCategory = "food-category";

    /// <summary>Minutes after local midnight, which the builder shows as a time of day.</summary>
    public const string MinuteOfDay = "minute-of-day";

    public string Kind { get; } = kind;
}
