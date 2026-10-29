using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp.Tools;

[McpServerToolType]
internal sealed class StatusTools
{
    [McpServerTool(Name = "get_current_status", Title = "Current status", ReadOnly = true, OpenWorld = false)]
    [Description(
        "The latest glucose reading (in the user's display units and mg/dL, with trend and age), "
        + "insulin and carbs on board, the pump mode and the active profile. Fields the token's "
        + "scopes do not cover are left out and named.")]
    [McpRequireScope(Scope.GlucoseRead)]
    public static async Task<CallToolResult> GetCurrentStatus(
        IHttpContextAccessor httpContextAccessor,
        ICanonicalGlucoseService glucose,
        IIobCobReader iobCob,
        IStateSpanService stateSpans,
        IActiveProfileResolver profiles,
        ITenantAppearanceReader appearance,
        ITenantAccessor tenant,
        McpToolReadLog readLog,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        var now = time.GetUtcNow();
        var useMmol = await GlucoseUnits.UsesMmolAsync(appearance, tenant, cancellationToken);
        var text = new StringBuilder();
        var omitted = new List<string>();

        var latest = await glucose.GetLatestAsync(cancellationToken);
        readLog.Record("glucose", latest is null ? 0 : 1);
        if (latest is null)
        {
            text.Append("Glucose: no reading found.").Append('\n');
        }
        else
        {
            var age = Math.Max((int)Math.Floor((now.UtcDateTime - latest.Timestamp).TotalMinutes), 0);
            var display = useMmol
                ? $"{GlucoseSummary.Format(latest.Mgdl, true)} mmol/L ({GlucoseSummary.Format(latest.Mgdl, false)} mg/dL)"
                : $"{GlucoseSummary.Format(latest.Mgdl, false)} mg/dL";
            text.Append(CultureInfo.InvariantCulture,
                $"Glucose: {display}, trend {latest.Direction?.ToString() ?? "unknown"}, "
                + $"{age} {(age == 1 ? "minute" : "minutes")} ago.").Append('\n');
        }

        if (http.HasScope(Scope.TreatmentsRead))
        {
            var reading = await iobCob.ReadTotalAsync(now.ToUnixTimeMilliseconds(), live: true, cancellationToken);
            readLog.Record("iob_cob", 1);
            text.Append(reading.HasIob
                ? string.Create(CultureInfo.InvariantCulture, $"Insulin on board: {reading.Iob.Iob:0.00} U.")
                : "Insulin on board: no data.").Append('\n');
            text.Append(reading.HasCob
                ? string.Create(CultureInfo.InvariantCulture, $"Carbs on board: {reading.Cob!.Cob:0} g.")
                : "Carbs on board: no data.").Append('\n');
        }
        else
        {
            omitted.Add("insulin and carbs on board");
        }

        if (http.HasScope(Scope.DevicesRead))
        {
            var mode = await stateSpans.GetCurrentPumpModeAsync(cancellationToken);
            readLog.Record("pump_mode", mode is null ? 0 : 1);
            text.Append($"Pump mode: {mode?.ToString() ?? "unknown"}.").Append('\n');
        }
        else
        {
            omitted.Add("pump mode");
        }

        if (http.HasScope(Scope.TherapyRead))
        {
            var profile = await profiles.GetActiveProfileNameAsync(now.ToUnixTimeMilliseconds(), cancellationToken);
            readLog.Record("profile", profile is null ? 0 : 1);
            text.Append($"Active profile: {profile ?? "none"}.").Append('\n');
        }
        else
        {
            omitted.Add("active profile");
        }

        if (omitted.Count > 0)
            text.Append($"Not included (scope not granted): {string.Join(", ", omitted)}.").Append('\n');

        return new CallToolResult { Content = [new TextContentBlock { Text = text.ToString().TrimEnd() }] };
    }
}
