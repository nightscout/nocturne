/** What the API's custom report run answers for one stat-tile panel, cut down. */
export function statTilesResult(title: string | null = "Two weeks") {
  const from = Date.UTC(2026, 9, 15);
  return {
    spec: { version: 1, title },
    units: "mg/dl",
    summary: "joined",
    warnings: [],
    timeZone: "UTC",
    openUrl: null,
    panels: [
      {
        id: "stats",
        title: "Statistics",
        dataset: "glucose_statistics",
        primitive: "stat_tiles",
        bindings: { columns: ["timeInRangePercent"] },
        status: "ok",
        window: { from, to: from + 14 * 86_400_000 },
        fields: [{ name: "timeInRangePercent", label: "Time in range", kind: "percent", unit: "%" }],
        rows: [{ timeInRangePercent: 72.4 }],
        summary: "Readings: 4025.",
        omittedScopes: [],
        link: "https://dev.nocturne.localhost:1612/reports/glucose-distribution?from=2026-10-15&to=2026-10-28",
      },
    ],
  };
}

/** A normalised spec as the API returns it beside the result. */
export const normalisedSpec = {
  version: 1,
  title: "Two weeks",
  panels: [{ id: "stats", dataset: { name: "glucose_statistics" }, primitive: "stat_tiles", range: { hours: 336 } }],
};

/** A run of one stat-tile panel and one glucose line: the line can be saved as an image, the tiles cannot. */
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
        dataset: "glucose_series",
        primitive: "time_series",
        bindings: { x: "time", y: ["glucose"] },
        status: "ok",
        window: { from, to },
        fields: [
          { name: "time", label: "Time", kind: "time", unit: null },
          { name: "glucose", label: "Glucose", kind: "number", unit: "mg/dL", color: "glucose-in-range" },
        ],
        rows: [{ time: from, glucose: 110 }, { time: to, glucose: 140 }],
        summary: "Readings: 2.",
        omittedScopes: [],
        link: null,
      },
    ],
  };
}
