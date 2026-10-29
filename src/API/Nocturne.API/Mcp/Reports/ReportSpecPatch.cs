using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nocturne.API.Mcp.Reports;

/// <summary>
/// What the report view changes in a spec it drew: the range of every panel, or a panel's range
/// and dataset parameters. <c>refine_report</c> applies it to the normalised spec and validates the
/// result as <c>render_report</c> validates a spec, so a patch can reach nothing a spec cannot.
/// </summary>
[Description("Changes to a report spec: a new range for every panel, or per panel a new range and dataset parameters.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportSpecPatch
{
    [Description("The range of every panel. Optional.")]
    public ReportRange? Range { get; init; }

    [Description("Changes to single panels, by id. Optional.")]
    public IReadOnlyList<ReportPanelPatch>? Panels { get; init; }
}

[Description("Changes to one panel.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportPanelPatch
{
    [Description("The id of the panel to change, as the spec names it.")]
    [JsonRequired]
    public string Id { get; init; } = "";

    [Description("The panel's new range. Optional.")]
    public ReportRange? Range { get; init; }

    [Description("Dataset parameters to set, by name, over the panel's own. The dataset name cannot change. Optional.")]
    public JsonElement? Dataset { get; init; }
}

internal static class ReportSpecPatches
{
    /// <summary>
    /// <paramref name="spec"/> with <paramref name="patch"/> applied, as JSON for the validator, or
    /// the errors that stopped it: a patch that names no panel of the spec, changes a dataset's name,
    /// or is not shaped as a patch.
    /// </summary>
    public static (JsonElement? Spec, IReadOnlyList<ReportSpecError> Errors) Apply(JsonElement spec, JsonElement patch)
    {
        var errors = new List<ReportSpecError>();
        if (spec.ValueKind != JsonValueKind.Object || JsonNode.Parse(spec.GetRawText()) is not JsonObject root)
            return (null, [new ReportSpecError("spec", "Send the spec that render_report returned.")]);
        if (patch.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return (spec, errors);
        if (patch.ValueKind != JsonValueKind.Object)
            return (null, [new ReportSpecError("patch", "Send the patch as an object with 'range' or 'panels'.")]);

        foreach (var property in patch.EnumerateObject())
        {
            if (property.Name is not ("range" or "panels"))
                errors.Add(new ReportSpecError($"patch.{property.Name}", "Not a known key. Keys here: range, panels."));
        }

        var panels = root["panels"] as JsonArray ?? [];
        if (patch.TryGetProperty("range", out var range) && range.ValueKind != JsonValueKind.Null)
        {
            root["range"] = JsonNode.Parse(range.GetRawText());
            foreach (var panel in panels.OfType<JsonObject>())
                panel["range"] = JsonNode.Parse(range.GetRawText());
        }

        if (patch.TryGetProperty("panels", out var panelPatches) && panelPatches.ValueKind != JsonValueKind.Null)
        {
            if (panelPatches.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new ReportSpecError("patch.panels", "Send a list of panel changes."));
                return (null, errors);
            }

            var index = 0;
            foreach (var change in panelPatches.EnumerateArray())
            {
                var at = $"patch.panels[{index++}]";
                if (change.ValueKind != JsonValueKind.Object
                    || !change.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                {
                    errors.Add(new ReportSpecError(at, "Send an object with the panel's 'id'."));
                    continue;
                }
                var id = idElement.GetString();
                if (panels.OfType<JsonObject>().FirstOrDefault(p => p["id"]?.GetValueKind() == JsonValueKind.String && p["id"]!.GetValue<string>() == id) is not { } panel)
                {
                    errors.Add(new ReportSpecError($"{at}.id", "No panel of the spec has this id."));
                    continue;
                }
                foreach (var property in change.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "id":
                            break;
                        case "range":
                            panel["range"] = JsonNode.Parse(property.Value.GetRawText());
                            break;
                        case "dataset" when property.Value.ValueKind == JsonValueKind.Object && panel["dataset"] is JsonObject dataset:
                            foreach (var parameter in property.Value.EnumerateObject())
                            {
                                if (parameter.Name == "name"
                                    && (parameter.Value.ValueKind != JsonValueKind.String || parameter.Value.GetString() != dataset["name"]?.ToString()))
                                    errors.Add(new ReportSpecError($"{at}.dataset.name", "A refinement cannot change the dataset. Call render_report for another."));
                                else
                                    dataset[parameter.Name] = JsonNode.Parse(parameter.Value.GetRawText());
                            }
                            break;
                        case "dataset":
                            errors.Add(new ReportSpecError($"{at}.dataset", "Send the parameters to change as an object."));
                            break;
                        default:
                            errors.Add(new ReportSpecError($"{at}.{property.Name}", "Not a known key. Keys here: id, range, dataset."));
                            break;
                    }
                }
            }
        }

        return errors.Count > 0 ? (null, errors) : (JsonSerializer.SerializeToElement(root), errors);
    }
}
