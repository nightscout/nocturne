using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using static Nocturne.API.Reports.Datasets.LoopAnalysisDatasetRun;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// The Settings evidence page (<c>GET /api/v4/loop-analysis/settings-evidence</c>) as a dataset:
/// one row per figure, the basal per two-hour block and the ISF and carb ratio per time-of-day
/// block. Like the page, it is withheld whole without glucose, treatments and therapy, and for a
/// history-clamped caller. Each figure is what the data showed beside the profile's value; the
/// summary says so in those words and never what to set.
/// </summary>
internal sealed class SettingsEvidenceDataset : IReportDataset
{
    public string Name => SettingsEvidenceParameters.Name;

    public string Label => "Settings evidence";

    public string Description => "The basal, ISF and carb ratio that your data shows, beside the values of your profile.";

    public Type ParametersType => typeof(SettingsEvidenceParameters);

    public string RequiredScope => Scope.GlucoseRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("setting", "Setting", ReportFieldKind.Category, null, "basal, isf or carb_ratio."),
        new("block", "Block", ReportFieldKind.Category, null, "The local time block, HH:mm-HH:mm."),
        new("startMinute", "Block start", ReportFieldKind.TimeOfDay, "min", "Start of the block, minutes after local midnight."),
        new("basalObserved", "Basal observed", ReportFieldKind.Number, "U/h", "The basal need the data showed in the block.", Color: ChartColor.InsulinBasal, Aggregate: ReportAggregate.Mean),
        new("basalProfile", "Basal in profile", ReportFieldKind.Number, "U/h", "The profile's basal rate over the block.", Aggregate: ReportAggregate.Mean),
        new("isfObserved", "ISF observed", ReportFieldKind.Number, "glucose/U", "The ISF the data showed in the block.", Color: ChartColor.InsulinBolus, Aggregate: ReportAggregate.Mean),
        new("isfProfile", "ISF in profile", ReportFieldKind.Number, "glucose/U", "The profile's ISF over the block.", Aggregate: ReportAggregate.Mean),
        new("carbRatioObserved", "Carb ratio observed", ReportFieldKind.Number, "g/U", "The carb ratio the data showed in the block.", Color: ChartColor.Carbs, Aggregate: ReportAggregate.Mean),
        new("carbRatioProfile", "Carb ratio in profile", ReportFieldKind.Number, "g/U", "The profile's carb ratio over the block.", Aggregate: ReportAggregate.Mean),
        new("episodes", "Episodes", ReportFieldKind.Number, null, "Episodes (basal: days) that qualified.", Aggregate: ReportAggregate.Sum),
        new("spread", "Middle half", ReportFieldKind.Category, null, "The 25th to 75th percentile of the episodes' values, with the unit."),
        new("confidence", "Confidence", ReportFieldKind.Category, null, "Low, Moderate or High; empty where there is no value."),
        new("confidenceReasons", "Why not higher", ReportFieldKind.Category, null, "What keeps the confidence below High."),
        new("noValueReason", "Why no value", ReportFieldKind.Category, null, "Why the figure has no observed value, where it has none."),
        new("rejected", "Rejected", ReportFieldKind.Number, null, "Candidate episodes the rules rejected.", Aggregate: ReportAggregate.Sum),
        new("rejections", "Rejected by", ReportFieldKind.Category, null, "Each rule that rejected episodes, with its count."),
    ];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) => mark is ReportMark.Line or ReportMark.Points
        ? new ReportLayerBindings { X = "startMinute", Y = ["basalObserved", "basalProfile"] }
        : null;

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Line, ReportMark.Points];

    public TimeSpan DefaultRange => TimeSpan.FromDays(30);

    public TimeSpan MaxRange => LoopAnalysisDatasetRun.MaxRange;

    public int PointBudget => 20;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters)
    {
        var p = (SettingsEvidenceParameters)parameters;
        if (p.Setting is { } setting && !Enum.IsDefined(setting))
            yield return ("setting", "Send basal, isf or carb_ratio.");
        if (p.ProfileName is { Length: > LoopAnalysisDatasetParameters.MaxProfileNameLength })
            yield return ("profileName", $"Use {LoopAnalysisDatasetParameters.MaxProfileNameLength} characters or fewer.");
    }

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var parameters = (SettingsEvidenceParameters)request.Parameters;
        var resolved = await ResolveAsync(request, parameters, null, cancellationToken);

        SettingsEvidenceWithheldReason? withheld =
            LoopAnalysisReadScopeGuard.WithheldSettingsEvidence(request.GrantedScopes).Count > 0
                ? SettingsEvidenceWithheldReason.ReadScopes
                : resolved.HistoryClamped ? SettingsEvidenceWithheldReason.HistoryClamped : null;

        var report = withheld is null && resolved.Filter is { } filter && resolved.Zone is { } zone
            ? await request.Services.GetRequiredService<ILoopAnalysisService>().GetSettingsEvidenceAsync(filter, zone, cancellationToken)
            : new SettingsEvidenceReport { WithheldReason = withheld };
        report = Serve(resolved, report, request);

        var rows = report.Primary is { } primary ? Rows(primary, parameters.Setting, request.UseMmol) : [];
        // The figures come from glucose, insulin and the profile together; the rows are figures, not
        // records, so no count is given.
        if (report.Primary is not null)
        {
            request.ReadLog.Record("glucose", 0);
            request.ReadLog.Record("treatments", 0);
            request.ReadLog.Record("profiles", 0);
        }

        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = Summarise(report, rows, request.UseMmol),
            Facts = FactsOf(rows),
            Sentence = report.WithheldReason is not null || report.TimeZoneUnavailableReason is not null
                ? Summarise(report, rows, request.UseMmol)
                : "Values the data showed, next to the profile's values: observations from the range, not instructions.",
            Context = ContextLine(report, ReportFacts.Plural(rows.Count, "figure")),
            Omitted = report.Primary is null && report.WithheldReason == SettingsEvidenceWithheldReason.ReadScopes
                ? [.. Fields.Select(f => new ReportOmittedField(f.Name, ScopeOf(LoopAnalysisSeries.SettingsEvidence)))]
                : [],
            Window = Window(report),
        };
    }

    private static List<IReadOnlyDictionary<string, object?>> Rows(SettingsEvidenceRange range, SettingsEvidenceSetting? only, bool useMmol)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        void Add(SettingsEvidenceSetting setting, SettingsEvidenceFigure figure, string observed, string profile, Func<double?, double?> scale, string unit)
        {
            if (only is not null && only != setting)
                return;
            rows.Add(new Dictionary<string, object?>
            {
                ["setting"] = Wire(setting),
                ["block"] = $"{Clock(figure.StartHour * 60)}-{Clock(figure.EndHour * 60)}",
                ["startMinute"] = figure.StartHour * 60,
                [observed] = scale(figure.Observed),
                [profile] = scale(figure.Profile),
                ["episodes"] = figure.N,
                ["spread"] = figure.Spread is { } s ? $"{Number(scale(s.P25)!.Value)} to {Number(scale(s.P75)!.Value)} {unit}" : null,
                ["confidence"] = figure.Confidence?.ToString(),
                ["confidenceReasons"] = figure.ConfidenceReasons.Count > 0 ? string.Join(", ", figure.ConfidenceReasons.Select(r => Words(r))) : null,
                ["noValueReason"] = figure.UnavailableReason is { } reason ? Words(reason) : null,
                ["rejected"] = figure.Rejections.Sum(r => r.Count),
                ["rejections"] = figure.Rejections.Any(r => r.Count > 0)
                    ? string.Join(", ", figure.Rejections.Where(r => r.Count > 0).OrderByDescending(r => r.Count).Select(r => $"{Words(r.Rule)} {r.Count}"))
                    : null,
            });
        }

        var glucoseUnit = (useMmol ? "mmol/L" : "mg/dL") + "/U";
        foreach (var block in range.Basal)
            Add(SettingsEvidenceSetting.Basal, block, "basalObserved", "basalProfile", v => Round(v, 3), "U/h");
        foreach (var block in range.Isf)
            Add(SettingsEvidenceSetting.Isf, block, "isfObserved", "isfProfile", v => Glucose(v, useMmol), glucoseUnit);
        foreach (var block in range.CarbRatio)
            Add(SettingsEvidenceSetting.CarbRatio, block, "carbRatioObserved", "carbRatioProfile", v => Round(v, 1), "g/U");
        return rows;
    }

    private static string Wire(SettingsEvidenceSetting setting) => setting switch
    {
        SettingsEvidenceSetting.Basal => "basal",
        SettingsEvidenceSetting.Isf => "isf",
        _ => "carb_ratio",
    };

    /// <summary>An enum member's name as lower-case words: <c>CarbsBefore</c> becomes "carbs before".</summary>
    internal static string Words(Enum value)
    {
        var name = value.ToString();
        var words = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            if (char.IsUpper(c) && words.Length > 0)
                words.Append(' ');
            words.Append(char.ToLowerInvariant(c));
        }
        return words.ToString().Replace("iob", "IOB", StringComparison.Ordinal).Replace("isf", "ISF", StringComparison.Ordinal);
    }

    /// <summary>For each setting, the blocks with an observed value out of all its blocks.</summary>
    internal static IReadOnlyList<ReportFact> FactsOf(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
    [
        .. rows.GroupBy(r => (string)r["setting"]!).Select(g =>
        {
            var observed = g.Key switch { "basal" => "basalObserved", "isf" => "isfObserved", _ => "carbRatioObserved" };
            var name = g.Key switch { "basal" => "Basal", "isf" => "ISF", _ => "Carb ratio" };
            return new ReportFact(name, $"{g.Count(r => r.GetValueOrDefault(observed) is double)} of {g.Count()}", "blocks", "with an observed value");
        }),
    ];

    /// <summary>
    /// What the data showed beside the profile, figure by figure. Each line reads "observed X next to
    /// the profile's Y", as the calculator states its figures: an observation, never what to set.
    /// </summary>
    internal static string Summarise(SettingsEvidenceReport report, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, bool useMmol)
    {
        const string what = "Settings evidence";
        switch (report.WithheldReason)
        {
            case SettingsEvidenceWithheldReason.ReadScopes:
                return "Settings evidence is not shown: it reads glucose, insulin and the profile's settings together, and this token "
                    + "does not hold all of glucose.read, treatments.read and therapy.read.";
            case SettingsEvidenceWithheldReason.HistoryClamped:
                return "Settings evidence is not shown: this token reads only the last 24 hours, and every figure looks back hours "
                    + "before its window, so it would read what it cannot see.";
        }
        if (NoTimeZone(report, what) is { } noZone)
            return noZone;

        var lines = new List<string>(Context(report, what))
        {
            "Values the data showed, next to the profile's values. These are observations from the range, not instructions.",
        };
        var glucoseUnit = (useMmol ? "mmol/L" : "mg/dL") + "/U";
        foreach (var row in rows)
        {
            var setting = (string)row["setting"]!;
            var (name, observed, profile, unit) = setting switch
            {
                "basal" => ("Basal", "basalObserved", "basalProfile", "U/h"),
                "isf" => ("ISF", "isfObserved", "isfProfile", glucoseUnit),
                _ => ("Carb ratio", "carbRatioObserved", "carbRatioProfile", "g/U"),
            };
            var head = $"{name} {row["block"]}: ";
            var profileText = row.GetValueOrDefault(profile) is double p ? $"the profile's {Number(p)} {unit}" : "no profile value";
            if (row.GetValueOrDefault(observed) is double o)
            {
                var reasons = row["confidenceReasons"] is string r ? $" ({r})" : "";
                lines.Add($"{head}observed {Number(o)} {unit} next to {profileText}. {row["episodes"]} episodes, "
                    + $"middle half {row["spread"]}. Confidence {((string?)row["confidence"])?.ToLowerInvariant()}{reasons}.");
            }
            else
            {
                lines.Add($"{head}no observed value ({row["noValueReason"] ?? "no data"}), next to {profileText}. {row["episodes"]} episodes qualified.");
            }
        }
        if (rows.Count == 0)
            lines.Add("No figures in this range.");
        return string.Join('\n', lines);
    }
}
