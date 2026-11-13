/** What the API's custom report run answers for one stat-tile panel, cut down. */
export function statTilesResult(title: string | null = "Two weeks") {
  const from = Date.UTC(2026, 9, 15);
  return {
    spec: { version: 1, title },
    units: "mg/dl",
    summary: "joined",
    warnings: [],
    timeZone: "UTC",
    panels: [
      {
        id: "stats",
        title: "Statistics",
        dataset: "glucose_statistics",
        kind: "stat_tiles",
        tracks: [],
        bindings: { columns: ["timeInRangePercent"] },
        status: "ok",
        window: { from, to: from + 14 * 86_400_000 },
        fields: [{ name: "timeInRangePercent", label: "Time in range", kind: "percent", unit: "%" }],
        rows: [{ timeInRangePercent: 72.4 }],
        summary: "Readings: 4025.",
        link: "https://dev.nocturne.localhost:1612/reports/glucose-distribution?from=2026-10-15&to=2026-10-28",
      },
    ],
  };
}

/** A normalised spec as the API returns it beside the result. */
export const normalisedSpec = {
  version: 1,
  title: "Two weeks",
  panels: [{ id: "stats", kind: "stat_tiles", dataset: { name: "glucose_statistics" }, range: { hours: 336 } }],
};

/** A run of one stat-tile panel and one chart of a glucose line: the line can be saved as an image, the tiles cannot. */
export function chartResult() {
  const result = statTilesResult("Glucose");
  const { from, to } = result.panels[0].window;
  return {
    ...result,
    panels: [
      ...result.panels,
      {
        id: "line",
        title: "Glucose and target range",
        kind: "chart",
        xKind: "time",
        status: "ok",
        window: { from, to },
        fields: [],
        rows: [],
        tracks: [
          {
            id: "t1",
            height: 1,
            layers: [
              {
                id: "l1",
                title: "Glucose readings · Glucose",
                mark: "line",
                bindings: { x: "time", y: ["glucose"] },
                xKind: "time",
                unit: "mg/dL",
                side: "left",
                status: "ok",
                window: { from, to },
                fields: [
                  { name: "time", label: "Time", kind: "time", unit: null },
                  { name: "glucose", label: "Glucose", kind: "number", unit: "mg/dL", color: "glucose-in-range" },
                ],
                rows: [{ time: from, glucose: 110 }, { time: to, glucose: 140 }],
              },
            ],
          },
        ],
        summary: "Readings: 2.",
        link: null,
      },
    ],
  };
}

type SpecPanels = { panels?: { id: string; tracks?: { id: string; layers: { id: string }[] }[] }[] };

/**
 * The API's run of any chart spec, as a stand-in: each chart panel's layers drawn as a glucose line
 * over two readings, each other panel as one stat tile. Enough to draw a preview of every block.
 */
export function runOf(spec: Record<string, unknown>) {
  const from = Date.UTC(2026, 9, 29, 6);
  const to = from + 6 * 3_600_000;
  const given: SpecPanels["panels"] = Array.isArray(spec.panels) ? spec.panels : [];
  const panels = (given ?? []).map((p) =>
    p.tracks
      ? {
          id: p.id,
          title: "Chart",
          kind: "chart",
          xKind: "time",
          status: "ok",
          window: { from, to },
          fields: [],
          rows: [],
          summary: "Glucose.",
          facts: [{ label: "Readings", value: "2" }],
          tracks: p.tracks.map((t) => ({
            id: t.id,
            height: 1,
            layers: t.layers.map((l) => ({
              id: l.id,
              title: l.id,
              mark: "line",
              bindings: { x: "time", y: ["glucose"] },
              xKind: "time",
              unit: "mg/dL",
              side: "left",
              status: "ok",
              window: { from, to },
              fields: [
                { name: "time", label: "Time", kind: "time" },
                { name: "glucose", label: "Glucose", kind: "number", unit: "mg/dL", color: "glucose-in-range" },
              ],
              rows: [{ time: from, glucose: 110 }, { time: to, glucose: 140 }],
            })),
          })),
        }
      : { ...statTilesResult().panels[0], id: p.id },
  );
  return { result: { spec: { version: 1 }, units: "mg/dl", summary: "", warnings: [], timeZone: "UTC", panels }, spec, errors: [] };
}
