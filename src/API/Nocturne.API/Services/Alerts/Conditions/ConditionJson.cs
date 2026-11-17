using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.API.Services.Alerts.Conditions;

/// <summary>
/// JSON options for the condition models (<see cref="Nocturne.Core.Models.ConditionNode"/> and its
/// payloads): snake_case property names, matched exactly (docs/alerts/engine-semantics.md §1.1).
/// </summary>
internal static class ConditionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
