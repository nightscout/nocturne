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
