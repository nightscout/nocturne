using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// JSON options shared by every <see cref="Nocturne.Core.Contracts.Alerts.IConditionEvaluator"/>.
/// Matches the shape used by the controllers and Zod schemas: snake_case property naming,
/// case-insensitive read.
/// </summary>
internal static class EvaluatorJson
{
    public static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };
        // System enums can't carry [JsonConverter] attributes; register the
        // string converter here so leaves like DayOfWeekCondition accept day
        // names ("monday") on the wire instead of forcing integer indices.
        // The converter is bidirectional — integer payloads still parse.
        options.Converters.Add(new JsonStringEnumConverter<DayOfWeek>());
        return options;
    }
}
