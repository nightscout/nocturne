using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nocturne.API.Mcp.Reports;

/// <summary>
/// One step from a spec version to the next: renames of datasets, fields or parameters, and any
/// other change a spec saved in <see cref="FromVersion"/> needs to read as <c>FromVersion + 1</c>.
/// </summary>
internal sealed record ReportSpecRewrite(int FromVersion, Action<JsonObject> Apply);

/// <summary>
/// Brings a stored spec up to <see cref="ReportSpec.CurrentVersion"/> before the validator reads
/// it, one <see cref="ReportSpecRewrite"/> at a time. A spec with no path to the current version is
/// left as it is, and the validator refuses it with its version message.
/// </summary>
internal sealed class ReportSpecRewriter(IReadOnlyList<ReportSpecRewrite> steps)
{
    /// <summary>The rewrites of the spec format's history. Version 1 is the first, so there are none yet.</summary>
    public static readonly ReportSpecRewriter Current = new([]);

    public JsonElement Rewrite(JsonElement spec)
    {
        if (spec.ValueKind != JsonValueKind.Object
            || !spec.TryGetProperty("version", out var v)
            || v.ValueKind != JsonValueKind.Number
            || !v.TryGetInt32(out var version)
            || version >= ReportSpec.CurrentVersion)
            return spec;

        if (JsonNode.Parse(spec.GetRawText()) is not JsonObject node)
            return spec;

        while (version < ReportSpec.CurrentVersion && steps.FirstOrDefault(s => s.FromVersion == version) is { } step)
        {
            step.Apply(node);
            version++;
            node["version"] = version;
        }

        return JsonSerializer.SerializeToElement(node);
    }
}
