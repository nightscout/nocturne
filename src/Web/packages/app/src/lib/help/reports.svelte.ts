import { guide } from "./guide.svelte";
import { reportContext } from "./report-context.svelte";
import { GLUCOSE_REFERENCE_PATH } from "./reference-path";
import type { HelpArticle } from "./documentation-types";

export function reportArticles(): HelpArticle[] {
  const context = reportContext();
  const report = (
    href: string,
    title: string,
    intro: string,
    used: string,
    detail: string
  ) =>
    guide(
      href,
      title,
      intro,
      context[href].source,
      context[href].controls,
      used,
      detail,
      [...context[href].related, GLUCOSE_REFERENCE_PATH]
    );
  return [
    report(
      "/reports",
      "Reports overview",
      "Choose a report to explore glucose, treatments, or activity.",
      "The overview leads to detailed reports using the same stored data.",
      "Each report needs its own data types. Opening a report does not enable an import."
    ),
    report(
      "/reports/executive-summary",
      "Executive Summary",
      "A short view of glucose, time in range, variability, and coverage.",
      "Start here, then open detailed reports to understand patterns behind the summary.",
      "Indicators describe the selected data. They do not identify the cause of a change."
    ),
    guide(
      "/reports/loop-analysis",
      "Loop Analysis",
      "Explore recorded loop activity alongside glucose, insulin, meals and predictions.",
      "Imported glucose, treatment, device-status and prediction records provide the report context. Availability depends on what your loop uploads.",
      "Choose the report dates and filters, then switch between Daily Profile, Meals and Events, Prediction Accuracy, Loop Health and Settings Evidence.",
      "Compare timing and coverage across the pages, then inspect individual days or events to understand the summaries.",
      "Missing uploads and incomplete history limit the results. Comparisons describe observations and do not establish a treatment effect or recommend settings changes.",
      ["/reports/readings", "/reports/insulin-delivery", GLUCOSE_REFERENCE_PATH]
    ),
    report(
      "/reports/agp",
      "AGP",
      "The Ambulatory Glucose Profile folds many days into one typical day.",
      "The middle line and percentile bands show when glucose is usually higher, lower, or less predictable.",
      "Percentiles describe the spread of readings at a time of day. This is not one actual day's trace. Uneven coverage can distort it."
    ),
    report(
      "/reports/glucose-distribution",
      "Glucose Distribution",
      "See how often readings fall in each range and how this changes by hour.",
      "The histogram and variability summaries explain the overall time-in-range result.",
      "The histogram counts readings. A broad distribution means more variation. Estimated A1c is calculated from glucose, not measured in blood."
    ),
    report(
      "/reports/ehba1c",
      "eHbA1c",
      "Compare glucose-based estimated A1c with laboratory HbA1c results.",
      "Saved laboratory results appear in this timeline beside the glucose estimate.",
      "Use the lab-result controls here to add the blood-draw date, value, note, or remove an entry. A lab measurement and an estimate are different and may disagree."
    ),
    report(
      "/reports/year-overview",
      "Year Overview",
      "A calendar heatmap shows changes across a whole year.",
      "Day and month summaries help you choose a period for a detailed report.",
      "Use the year and metric filters here. A day without usable data must not be interpreted as a good result."
    ),
    report(
      "/reports/readings",
      "Readings",
      "Explore the recorded glucose trace and its treatment context.",
      "These readings also feed the dashboard, calendar, and glucose statistics.",
      "Use the chart's date and display controls. Points are samples. A connecting line is not an extra measurement."
    ),
    report(
      "/reports/day-in-review",
      "Day in Review",
      "Bring one day's glucose and events together on a timeline.",
      "Inspect the timing behind daily patterns or unusual readings.",
      "Select the day here. Events only appear if recorded and imported. Nearby events do not prove cause and effect."
    ),
    report(
      "/reports/week-to-week",
      "Week to Week",
      "Compare weekday glucose patterns across weeks.",
      "Weekday averages help you explore differences between regular routines.",
      "The same weekday can have unequal coverage between weeks. An average can hide short highs and lows."
    ),
    report(
      "/reports/comparison",
      "Comparison",
      "Compare two periods with the same report measures.",
      "Find differences before exploring individual days.",
      "Choose both periods here. Different devices, coverage, or durations can affect the comparison. A difference does not prove a treatment effect."
    ),
    report(
      "/reports/hourly-stats",
      "Hourly Patterns",
      "Group readings by hour to find recurring highs, lows, and variation.",
      "Hourly range bars help you choose a time to investigate on the glucose chart.",
      "Each bar describes readings in that hour across the chosen days. Tight range is part of in range: do not add their percentages together."
    ),
    report(
      "/reports/basal-analysis",
      "Basal Analysis",
      "Inspect basal delivery together with glucose patterns.",
      "Delivery and schedule information help you inspect specific periods.",
      "Therapy profiles provide scheduled rates. Temporary basal and delivery records describe changes. Missing delivery records or historical profiles limit the analysis."
    ),
    report(
      "/reports/insulin-delivery",
      "Insulin Delivery",
      "Review basal and bolus totals, their proportions, and timing.",
      "Daily ratios and hourly summaries use stored insulin records.",
      "Bolus amounts and basal rates have different units. Basal totals depend on duration and available records. Incomplete imports can understate totals."
    ),
    report(
      "/reports/site-change-impact",
      "Site Change Impact",
      "Compare glucose around recorded infusion-site changes.",
      "Event windows help you inspect recurring patterns around changes.",
      "Correctly timed site-change events and glucose before and after are needed. Association does not prove that the change caused a glucose difference."
    ),
    report(
      "/reports/idp",
      "IDP",
      "Bring glucose patterns, insulin delivery, and therapy schedules together.",
      "The integrated daily profile combines summaries so you can inspect their timing.",
      "Scheduled basal is the therapy schedule, not necessarily insulin actually delivered. Missing profiles or treatments leave panels empty."
    ),
    guide(
      "/reports/data-quality",
      "Data Quality",
      "Check that recorded data is complete and usable.",
      "Stored glucose timestamps, device identities, and quality or noise information when supplied.",
      "Choose the report period. Settings â†’ Data Quality contains detection and maintenance controls. Each detailed quality report has its own filters.",
      "Coverage and reliability help you interpret reports and recognize import or sensor problems.",
      "A flag needs inspection. It does not prove a reading is wrong. Cleanup affects later reports, so inspect records before confirming.",
      [
        "/settings/data-quality",
        "/reports/data-quality/sensor-integrity",
        "/reports/data-quality/cgm-comparison",
        "/reports/data-quality/compression-lows",
      ]
    ),
    guide(
      "/reports/data-quality/sensor-integrity",
      "Sensor Integrity",
      "Inspect noisy windows and surrounding readings.",
      "Stored readings and available source noise metadata.",
      "Choose dates here. Configure detection in Settings â†’ Data Quality.",
      "Flags help you inspect questionable sections of glucose reports.",
      "Not every uploader supplies noise metadata. Noise or a low after a flagged window is not a diagnosis.",
      ["/settings/data-quality", "/reports/data-quality"]
    ),
    guide(
      "/reports/data-quality/cgm-comparison",
      "CGM Comparison",
      "Compare devices that recorded glucose near the same time.",
      "Readings paired between your selected device and reference.",
      "Choose Device, Reference, and Match within here. Manage sources in Settings â†’ Connectors & Apps.",
      "The scatter and agreement summaries show differences between paired readings.",
      "Unpaired readings are excluded. A wider tolerance allows paired readings farther apart in time. Your chosen reference is not guaranteed ground truth.",
      ["/settings/connectors", "/reports/data-quality"]
    ),
    guide(
      "/reports/data-quality/compression-lows",
      "Compression Lows",
      "Review suggested low-reading windows for a possible quality correction.",
      "Automatic detection examines stored glucose patterns using the detection settings.",
      "Set date and status filters here. Configure automatic detection and typical sleep times in Settings â†’ Data Quality. Inspect suggestions before accepting or dismissing.",
      "Accepted corrections affect subsequent charts and analysis.",
      "A suggestion can be mistaken and does not prove pressure caused a low. Check the preview before changing records.",
      ["/settings/data-quality", "/reports/data-quality"]
    ),
    guide(
      "/reports/steps",
      "Steps",
      "This guide covers Steps: counting steps and viewing your activity over time.",
      "Step-count records received from a supported source. Google Health can import steps available in your connected account.",
      "Choose dates above the report. Settings â†’ Connectors & Apps â†’ Google Health controls step imports and the historical start date.",
      "Daily totals and activity timing help you explore your routine alongside glucose.",
      "An empty day does not necessarily mean no steps. Check source data, import selection, progress, and dates. Overlapping source records can differ from your watch's total.",
      ["/settings/connectors/google-health", "/settings/connectors"]
    ),
    guide(
      "/reports/heart-rate",
      "Heart Rate",
      "This guide covers Heart Rate: your recorded heart rate and patterns over time.",
      "Device or service samples through a supported connector. Google Health reduces its samples to one stable average per minute.",
      "Choose dates here. Enable heart rate in Settings â†’ Connectors & Apps â†’ Google Health after checking your device's readings reach that account.",
      "The timeline and daily summaries show heart-rate patterns alongside glucose.",
      "Values are beats per minute. Sampling and averaging can hide brief changes. Missing readings are not a zero heart rate. This page defines no universal high or low threshold.",
      ["/settings/connectors/google-health", "/settings/connectors"]
    ),
    guide(
      "/reports/sleep",
      "Sleep",
      "Explore sleep sessions, timing, and stage composition.",
      "Imported sleep sessions and stages, including supported Google Health records.",
      "Choose dates and the view here. Open a night for details. Configure imports in Connectors & Apps â†’ Google Health.",
      "Compare nights and glucose during recorded sleep windows.",
      "Device stages are estimates. Missing stages stay missing. Google filters sessions by their end time, so a night can begin before the import start date.",
      ["/settings/connectors/google-health", "/reports/heart-rate"]
    ),
    guide(
      "/reports/treatments",
      "Treatments",
      "Browse, search, add, or edit diabetes events.",
      "Uploaded, imported, and manually saved insulin, carb, notes, and device events.",
      "Use category tabs and Search here. Add/edit and bulk deletion require write permission. Settings â†’ Therapy shows schedules; Connectors & Apps manages sources.",
      "Events appear on glucose timelines and feed insulin and carb analysis.",
      "Event time, units, and type matter. Deletion changes later analysis. A source can recreate records on import unless the applicable deletion protection prevents it.",
      ["/settings/profile", "/settings/connectors", "/reports/insulin-delivery"]
    ),
    guide(
      "/reports/battery",
      "Battery",
      "Review battery information uploaded by devices.",
      "Device-status records containing battery values.",
      "Choose dates here. Configure devices and uploaders in Connectors & Apps.",
      "Battery history helps identify devices needing charging or missing uploads.",
      "Devices use different formats and update intervals. Missing battery information means unknown, not empty.",
      ["/settings/connectors"]
    ),
    guide(
      "/reports/sleep/[date]",
      "Sleep night",
      "Inspect one night and its stages.",
      "The selected sleep session, stage records, and glucose during that window.",
      "Open a night from the Sleep report. Choose other dates there. Import settings are in Connectors & Apps â†’ Google Health.",
      "The hypnogram and glucose summary explain the overview in detail.",
      "Missing stages and glucose stay missing. Overlapping sessions and estimated stage labels can limit the summary.",
      ["/reports/sleep", "/settings/connectors/google-health"]
    ),
  ];
}
