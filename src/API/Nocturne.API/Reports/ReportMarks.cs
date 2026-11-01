namespace Nocturne.API.Reports;

/// <summary>One binding a mark takes: its key, its words, what it takes and the field kinds it accepts.</summary>
/// <param name="Input">field (one field), fields (a list), boolean or choice.</param>
/// <param name="Counts">For fields: the numbers it takes ([2, 4] for a band's edges); empty for any number from one.</param>
/// <param name="Required">Whether the layer must have it once the defaults are filled.</param>
internal sealed record ReportMarkSlot(string Key, string Label, string Input, IReadOnlyList<string> Accepts, IReadOnlyList<int> Counts, bool Required);

/// <summary>What each mark takes: its slots, the axes it lies along and whether it takes a vertical axis.</summary>
internal static class ReportMarks
{
    public static readonly IReadOnlyList<ReportMark> All = Enum.GetValues<ReportMark>();

    private static readonly IReadOnlyList<ReportXKind> Continuous =
        [ReportXKind.Time, ReportXKind.Offset, ReportXKind.TimeOfDay, ReportXKind.Day, ReportXKind.Value, ReportXKind.Lag];

    private static readonly IReadOnlyList<string> Numbers = ["number", "percent"];

    public static string Label(ReportMark mark) => mark switch
    {
        ReportMark.Line => "Line",
        ReportMark.Area => "Area",
        ReportMark.Band => "Band",
        ReportMark.Bars => "Bars",
        ReportMark.Points => "Points",
        ReportMark.Markers => "Treatment markers",
        ReportMark.Spans => "Spans in a lane",
        _ => throw new ArgumentOutOfRangeException(nameof(mark), mark, null),
    };

    /// <summary>The axes a layer of the mark may lie along.</summary>
    public static IReadOnlyList<ReportXKind> XKinds(ReportMark mark) => mark switch
    {
        ReportMark.Line or ReportMark.Area or ReportMark.Points => Continuous,
        ReportMark.Band => [ReportXKind.Time, ReportXKind.Offset, ReportXKind.TimeOfDay, ReportXKind.Day],
        ReportMark.Bars => [ReportXKind.Category, ReportXKind.Day, ReportXKind.TimeOfDay, ReportXKind.Time, ReportXKind.Offset, ReportXKind.Lag],
        ReportMark.Markers or ReportMark.Spans => [ReportXKind.Time],
        _ => throw new ArgumentOutOfRangeException(nameof(mark), mark, null),
    };

    /// <summary>Whether the mark draws on a vertical axis: markers and spans do not.</summary>
    public static bool HasAxis(ReportMark mark) => mark is not (ReportMark.Markers or ReportMark.Spans);

    private static IReadOnlyList<string> Axis(ReportMark mark) =>
        [.. XKinds(mark).Select(k => ReportWire.Name(Derived.ReportDerivedOps.FieldKindOf(k))).Distinct()];

    public static IReadOnlyList<ReportMarkSlot> Slots(ReportMark mark) => mark switch
    {
        ReportMark.Line =>
        [
            new("x", "Horizontal axis", "field", Axis(mark), [], true),
            new("y", "Lines", "fields", Numbers, [], true),
            new("series", "One line for each", "field", ["category"], [], false),
        ],
        ReportMark.Area =>
        [
            new("x", "Horizontal axis", "field", Axis(mark), [], true),
            new("y", "Areas", "fields", Numbers, [], true),
        ],
        ReportMark.Points =>
        [
            new("x", "Horizontal axis", "field", Axis(mark), [], true),
            new("y", "Points", "fields", Numbers, [], true),
            new("fit", "Line through the points", "field", Numbers, [], false),
        ],
        ReportMark.Band =>
        [
            new("x", "Horizontal axis", "field", Axis(mark), [], true),
            new("edges", "Band edges: low and high", "fields", Numbers, [2, 4], true),
            new("y", "Middle line", "fields", Numbers, [1], false),
            new("series", "One band for each", "field", ["category"], [], false),
        ],
        ReportMark.Bars =>
        [
            new("x", "Categories or axis", "field", Axis(mark), [], true),
            new("y", "Bars", "fields", Numbers, [], true),
            new("stacked", "Stack the bars", "boolean", [], [], false),
            new("orientation", "Bar direction", "choice", [], [], false),
        ],
        ReportMark.Markers =>
        [
            new("x", "Horizontal axis", "field", Axis(mark), [], true),
            new("y", "Markers", "fields", ["marker"], [], true),
        ],
        ReportMark.Spans =>
        [
            new("start", "Start", "field", ["time"], [], true),
            new("end", "End", "field", ["time"], [], true),
            new("category", "Lane", "field", ["category"], [], true),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(mark), mark, null),
    };

    /// <summary>Whether <paramref name="field"/> may fill <paramref name="slot"/>.</summary>
    public static bool Accepts(ReportMarkSlot slot, ReportField field) =>
        !field.Companion
        && (slot.Accepts.Contains("marker") ? field.Marker is not null : slot.Accepts.Contains(ReportWire.Name(field.Kind)));
}
