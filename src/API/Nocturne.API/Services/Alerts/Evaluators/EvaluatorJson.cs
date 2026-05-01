using System.Text.Json;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// JSON options shared by every <see cref="Nocturne.Core.Contracts.Alerts.IConditionEvaluator"/>.
/// Matches the shape used by the controllers and Zod schemas: snake_case property naming,
/// case-insensitive read.
/// </summary>
internal static class EvaluatorJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };
}
