export function reportContext(): Record<
  string,
  { source: string; controls: string; related: string[] }
> {
  return {
    "/reports": {
      source:
        "Stored glucose readings feed the overview's range summary and typical-day preview.",
      controls:
        "Use the date selector above the overview, then open a report card. Appearance changes display units. Connectors & Apps manages incoming records.",
      related: ["/reports/agp", "/reports/day-in-review"],
    },
    "/reports/executive-summary": {
      source:
        "Glucose readings in the selected period are summarized into headline measures and reliability information.",
      controls:
        "Choose the date range above the report. Appearance controls glucose units. Choose a detailed report to investigate a headline.",
      related: ["/reports/agp", "/reports/glucose-distribution"],
    },
    "/reports/agp": {
      source:
        "Recorded glucose from several days is grouped by time of day into a median and percentile envelope.",
      controls:
        "Select the report period above the page. Appearance changes glucose units. A personal target overlay comes from the imported therapy profile.",
      related: ["/reports/day-in-review", "/reports/hourly-stats"],
    },
    "/reports/glucose-distribution": {
      source:
        "Glucose samples are counted by value interval and grouped by hour for the hourly panel.",
      controls:
        "Use the report date selector. Appearance changes the displayed glucose units. Source and import problems are managed in Connectors & Apps.",
      related: ["/reports/hourly-stats", "/reports/data-quality"],
    },
    "/reports/ehba1c": {
      source:
        "Glucose-based estimates come from stored readings. Laboratory results come from the dated entries you save here.",
      controls:
        "Use the timeline's time-scale controls. The Lab results controls add or remove a dated result with its value and optional note. Appearance changes display units.",
      related: ["/reports/readings", "/reports/year-overview"],
    },
    "/reports/year-overview": {
      source:
        "Daily and monthly summaries are built from the stored data types and sources selected on this page.",
      controls:
        "Use the source and data-type filters. The legend controls metric, color palette, and focus range. These view filters do not change imported records.",
      related: ["/reports/day-in-review", "/reports/data-quality"],
    },
    "/reports/readings": {
      source:
        "The glucose chart uses recorded samples and available treatment/device tracks in the chosen interval.",
      controls:
        "Choose dates and visible chart tracks on this page. Appearance changes units. Connectors & Apps manages the records being displayed.",
      related: ["/reports/day-in-review", "/reports/treatments"],
    },
    "/reports/day-in-review": {
      source:
        "One day's recorded glucose, treatments, and available device context are aligned by timestamp.",
      controls:
        "Choose the day here. Correct treatment entries in Treatments. Manage profile context in Therapy and formatting in Appearance.",
      related: ["/reports/treatments", "/reports/readings"],
    },
    "/reports/week-to-week": {
      source:
        "Stored glucose is grouped by weekday across the selected period.",
      controls:
        "Use the date range above the report to choose the weeks. Appearance changes units. This page does not edit weekday targets or therapy schedules.",
      related: ["/reports/day-in-review", "/reports/agp"],
    },
    "/reports/comparison": {
      source:
        "Each side runs the same glucose analysis for its committed date window.",
      controls:
        "Choose a preset or set both periods and labels, then click Load. Swap exchanges the loaded periods immediately. Display units are in Appearance.",
      related: ["/reports/day-in-review", "/reports/data-quality"],
    },
    "/reports/hourly-stats": {
      source:
        "Glucose samples are grouped into local-hour buckets across the selected days.",
      controls:
        "Choose the date range above the report. The hourly summaries and table describe that selection. Appearance controls glucose units and time format.",
      related: ["/reports/readings", "/reports/agp"],
    },
    "/reports/basal-analysis": {
      source:
        "Imported basal delivery, temporary basal events, and available therapy schedules provide rate and duration context.",
      controls:
        "Choose the report period. Therapy shows the imported schedule. Connectors & Apps manages its source. This report does not edit pump settings.",
      related: ["/reports/insulin-delivery", "/settings/profile"],
    },
    "/reports/insulin-delivery": {
      source:
        "Recorded basal and bolus delivery is summarized by day and hour.",
      controls:
        "Choose the report period. Inspect treatment records in Treatments, schedules in Therapy, and delivery imports in Connectors & Apps.",
      related: ["/reports/treatments", "/reports/basal-analysis"],
    },
    "/reports/site-change-impact": {
      source:
        "Recorded infusion-site changes define the event windows used to compare surrounding glucose.",
      controls:
        "Choose the report period. Check site-change event timestamps in Treatments and any associated tracker history in Notifications & Trackers.",
      related: ["/reports/treatments", "/settings/trackers"],
    },
    "/reports/idp": {
      source:
        "Glucose statistics, recorded insulin delivery, and imported schedules provide the separate panels of the integrated profile.",
      controls:
        "Choose the report period. Therapy provides the profile and scheduled targets. Connectors & Apps manages actual incoming delivery records.",
      related: ["/reports/agp", "/reports/insulin-delivery"],
    },
  };
}
