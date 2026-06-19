Pre-computed data for dashboards, charts, and retrospective analysis.

Key endpoints:

- **Chart Data** — Returns *everything* the glucose chart needs in a single call: readings, IOB/COB series, basal delivery, treatment markers, state spans, system events, and tracker markers. Prefer this over calling individual endpoints.
- **Correlation** — Query across all V4 repositories by correlation ID to trace related records.
- **Data Overview** — Year-level availability and day-level record counts for heatmap visualisation.
- **Predictions** — Glucose forecasts from DeviceStatus sources (AAPS / Trio / Loop) or the OrefWasm engine.
- **Retrospective** — Day-in-review snapshots combining IOB, COB, glucose, basal timelines, and insulin delivery at specific points in time.
- **Statistics** — Aggregated statistics including glucose time-in-range, insulin delivery breakdowns, and AID system metrics.
- **Summary** — Widget-friendly data designed for mobile widgets, watch faces, and other constrained displays.
- **Analytics** — Transparency controls for analytics collection — view, configure, and opt out.
