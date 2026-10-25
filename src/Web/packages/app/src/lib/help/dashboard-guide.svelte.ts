import { WidgetId } from "$lib/api/generated/nocturne-api-client";
import {
  TOP_WIDGET_IDS,
  type TopWidgetId,
} from "$lib/components/dashboard/top-widget-ids";

export interface DashboardTopic {
  id: string;
  title: string;
  meaning: string;
  source: string;
  settings: (
    | "/settings/appearance"
    | "/settings/connectors"
    | "/settings/profile"
    | "/settings/trackers"
    | "/settings/data-quality"
    | "/settings/patient"
    | "/alerts"
  )[];
  availability: string;
  check: string;
}

export function dashboardTopics(): DashboardTopic[] {
  const top: Record<TopWidgetId, Omit<DashboardTopic, "id">> = {
    [WidgetId.BgDelta]: {
      title: "BG Delta",
      meaning:
        "The change beside the glucose value is the uploader's delta when supplied. Otherwise Nocturne subtracts the previous sensor reading from the newest one. Plus means higher. Minus means lower. It is not necessarily a five-minute difference when readings are farther apart.",
      source:
        "The two newest sensor readings, with any delta supplied by the uploader. The battery badge uses recent device battery reports. The connection indicator describes live browser updates.",
      settings: ["/settings/appearance", "/settings/connectors"],
      availability:
        "Select BG Delta as a top widget in Appearance. A meaningful change needs a supplied delta or two sensor readings. Battery information needs a recent battery report.",
      check:
        "Compare the timestamps of the two readings. If uploads stopped, check the source app and connector status. A live browser connection does not guarantee a recent sensor measurement.",
    },
    [WidgetId.LastUpdated]: {
      title: "Last Updated",
      meaning:
        "The age is time since the newest sensor reading's timestamp. The clock time identifies that reading. A battery badge can also show the lowest reported device battery and whether it is charging.",
      source:
        "Sensor reading timestamps and device battery reports from the last 30 minutes.",
      settings: ["/settings/appearance", "/settings/connectors"],
      availability:
        "Select Last Updated in Appearance. Battery details appear only when a device reports them. Their absence does not mean the battery is empty.",
      check:
        "Check that the uploader has recent readings and that the connector is syncing. Check device reporting separately if only the battery badge is absent.",
    },
    [WidgetId.ConnectionStatus]: {
      title: "Connection",
      meaning:
        "Connected means browser live updates are active. Connecting is a pending connection. Disconnected means live updates are unavailable and cached data may still be shown. Not available means this view has no permission for live updates. Messages counts received connection messages.",
      source:
        "The browser's real-time connection, not a direct connection to your sensor or pump.",
      settings: ["/settings/appearance", "/settings/connectors"],
      availability:
        "Select Connection in Appearance. Live updates also require access to this workspace.",
      check:
        "Check your browser connection first. Use Retry after a disconnection. If connected but readings are old, investigate the uploader or connector instead.",
    },
    [WidgetId.Meals]: {
      title: "Recent Meals",
      meaning:
        "Shows up to three of the newest carb entries from the last six hours. The large number totals those displayed entries, not all daily carb intake. Last identifies the newest displayed meal.",
      source: "Recorded carb entries with a positive amount.",
      settings: ["/settings/appearance", "/settings/connectors"],
      availability:
        "Select Recent Meals in Appearance. Import or record carb entries. Glucose readings alone cannot fill this widget.",
      check:
        "Check that the source shares carb records and that the entry is within the last six hours.",
    },
    [WidgetId.Trackers]: {
      title: "Trackers",
      meaning:
        "Shows up to four active trackers. The age is measured from their start time. Colors reflect the configured tracker schedule and reached urgency, not glucose ranges.",
      source:
        "Tracker definitions, active runs and their server-provided schedules.",
      settings: ["/settings/appearance", "/settings/trackers"],
      availability:
        "Select Trackers in Appearance, create a tracker and start a run. Its dashboard visibility must allow it to appear.",
      check:
        "Check the tracker's active run, start time, dashboard visibility and notification thresholds.",
    },
    [WidgetId.TirChart]: {
      title: "Time in Range",
      meaning:
        "The colored bar divides recorded glucose into report bands. The large percentage is in range. Below and Above combine their two respective bands. The comparison is a percentage-point difference from the 90-day result. Use the period button to switch between the last 24 hours and 90 days.",
      source:
        "Server glucose statistics and the shared report bands. The reading count shows how much recorded data supports the result.",
      settings: [
        "/settings/appearance",
        "/settings/data-quality",
        "/settings/connectors",
      ],
      availability:
        "Select Time in Range in Appearance. Statistics need enough glucose readings. Gaps affect how representative the result is.",
      check:
        "Check the reading count and Data Quality report. See Glucose ranges for the central definition of the bands. Personal targets and alert settings have different purposes.",
    },
    [WidgetId.DailySummary]: {
      title: "Today's Summary",
      meaning:
        "Summarizes loaded readings since local midnight: average, minimum, maximum, time in range and variability. CV expresses variation relative to the average. This widget currently labels CV below 33% Stable, above 50% Variable, and the values between them Moderate. These are summary labels, not trend arrows.",
      source:
        "Readings currently loaded in the browser. Its day starts at browser-local midnight. Incomplete loaded history can make this differ from a full server report.",
      settings: [
        "/settings/appearance",
        "/settings/patient",
        "/settings/connectors",
      ],
      availability:
        "Select Today's Summary in Appearance. At least one valid reading from today must be loaded.",
      check:
        "Compare the displayed reading count with your source. For a complete period, use the reports. Check timezone settings if the day boundary looks wrong.",
    },
    [WidgetId.Clock]: {
      title: "Clock",
      meaning:
        "The current local time, seconds and date. It is separate from the age of the newest reading.",
      source: "The browser clock and display locale.",
      settings: ["/settings/appearance", "/settings/patient"],
      availability:
        "Select Clock in Appearance. It does not require sensor or treatment data.",
      check:
        "Check your device clock and timezone if the time is unexpected. Patient timezone history is relevant to recorded events, not the browser clock itself.",
    },
    [WidgetId.Tdd]: {
      title: "Total Daily Dose",
      meaning:
        "Bolus totals manual bolus records. Auto totals algorithm bolus records, such as imported automatic or microboluses. It is not every automatic basal adjustment. Basal includes pump basal delivery and basal injections, excluding the Auto amount shown separately. Carbs is recorded carb intake. The ring uses the same insulin categories as the numbers.",
      source:
        "Server insulin-delivery statistics: manual boluses, algorithm boluses, basal delivery records and basal injections. The source's imported record classification determines the category.",
      settings: [
        "/settings/appearance",
        "/settings/connectors",
        "/settings/profile",
      ],
      availability:
        "Select Total Daily Dose in Appearance. Your source must send insulin records. Auto is shown only when algorithm bolus insulin is greater than zero. Both pump delivery and injected basal can contribute.",
      check:
        "The Today button currently means the preceding 24 hours ending at the statistics update time, not midnight to now. The 90-Day Avg view uses the preceding 90 days and displays daily averages. Check imported insulin categories and missing delivery history if a category is absent. These statistics also require sufficient glucose data.",
    },
  };
  return [
    {
      id: "glucose",
      title: "Current glucose",
      meaning:
        "The large value is the newest sensor measurement in your selected units. Its color comes from the server's status for that reading. The trend comes from the direction supplied with the reading. A change number alone does not create an arrow.",
      source:
        "The newest sensor reading, its supplied direction and the server's matching status. The difference uses a supplied delta or the previous sensor reading.",
      settings: [
        "/settings/appearance",
        "/settings/profile",
        "/settings/connectors",
        "/alerts",
      ],
      availability:
        "A sensor source supplies measurements and, when supported, trend directions. Unknown directions cannot be displayed as a known trend. A stale reading loses its active trend presentation.",
      check:
        "X minutes is time since the measurement timestamp, not time since opening the page or the last connector sync. If the reading is old, check the source and connector. Colors, chart bands, therapy targets and alert thresholds are explained centrally in Glucose ranges.",
    },
    ...TOP_WIDGET_IDS.map((id) => ({ id, ...top[id] })),
    {
      id: "pills",
      title: "Status pills",
      meaning:
        "IOB is estimated insulin still active. COB is estimated carbs still being absorbed. Basal is a rate, not a delivered daily total. Loop describes the latest algorithm status. Reservoir is the pump's reported remaining insulin. Tracker pills describe active tracker runs.",
      source:
        "Device or algorithm snapshots, pump reservoir reports and tracker records. These values cannot be reconstructed from glucose alone.",
      settings: [
        "/settings/trackers",
        "/settings/connectors",
        "/settings/profile",
      ],
      availability:
        "A compatible uploader must provide the corresponding device or loop fields. Some pumps do not report a numeric reservoir. Tracker pill visibility is configured per tracker.",
      check:
        "Check whether the source actually provides the missing field. Compare the device snapshot age, connector state and tracker visibility. A missing estimate is not a zero estimate.",
    },
    {
      id: "readings",
      title: "Recent readings",
      meaning:
        "Each row shows a recorded reading, its timestamp, difference and available trend. Rows let you inspect the individual measurements behind the latest value.",
      source: "The latest readings loaded from your sensor source.",
      settings: ["/settings/appearance", "/settings/connectors"],
      availability:
        "Enable the recent-readings dashboard section and import sensor readings.",
      check:
        "Check timestamp, source and units. If the source has newer readings, check sync status. A missing delta is shown as unavailable rather than a measured zero.",
    },
    {
      id: "treatments",
      title: "Recent treatments",
      meaning:
        "The list shows recent recorded events such as boluses, carbs, meter checks and device changes. These provide context for the glucose curve. They do not prove what caused a change.",
      source:
        "Imported or manually recorded treatment and device-event records.",
      settings: ["/settings/appearance", "/settings/connectors"],
      availability:
        "Enable the treatments dashboard section. The source must share the corresponding records, or you can record them yourself.",
      check:
        "Check the event time and whether your connector supports that record type. Inspect the real record on the Treatments page to correct it. This demo cannot edit records.",
    },
  ];
}

export function chartTopics(): DashboardTopic[] {
  const common: Pick<DashboardTopic, "settings" | "check"> = {
    settings: [
      "/settings/appearance",
      "/settings/connectors",
      "/settings/profile",
    ],
    check:
      "Check the source record and its timestamp, then the chart legend and dashboard settings. If the source has no corresponding data, enabling the display alone cannot create it.",
  };
  return [
    [
      "curve",
      "Glucose line and points",
      "The curve connects recorded sensor measurements. Points and hover details identify individual readings. Gaps mean missing observations, not known stable glucose.",
      "Sensor glucose and measurement timestamps.",
    ],
    [
      "axes",
      "Axes and time window",
      "The horizontal axis is time. The glucose axis uses your chosen units. Drag or zoom the overview to inspect a shorter period. Selecting a period changes only the demo.",
      "Measurement timestamps and the display's unit preference.",
    ],
    [
      "ranges",
      "Colors and target areas",
      "Colors identify the shared glucose bands. Threshold lines and personal target overlays provide different context. See Glucose ranges for boundary definitions instead of repeating them on every page.",
      "Chart thresholds and the therapy profile in force.",
    ],
    [
      "predictions",
      "Predictions",
      "Forecast lines or a cone show model estimates beyond recorded glucose. IOB, COB, UAM and zero-temp curves express different model assumptions. They are examples here. Future readings can differ.",
      "The prediction service needs supported glucose, therapy and treatment context. Enable predictions in the dashboard and algorithm settings.",
    ],
    [
      "basal",
      "Basal delivery",
      "The basal track shows insulin rate over time against the scheduled rate. Inferred delivery is distinguished from reported delivery. A rate needs a duration before it represents delivered units.",
      "Pump basal delivery and schedule records. Configure your therapy profile and upload delivery data.",
    ],
    [
      "iob",
      "Insulin on board",
      "IOB shows the source or model's estimate of insulin still active over time. It is distinct from Total Daily Dose.",
      "Algorithm/device snapshots with IOB values.",
    ],
    [
      "cob",
      "Carbs on board",
      "COB shows estimated carbs remaining to be absorbed. It is distinct from the total grams entered for a meal.",
      "Algorithm/device snapshots with COB values.",
    ],
    [
      "bolus",
      "Bolus markers",
      "Markers identify insulin events at their recorded times. Manual and algorithm boluses can have different styles and classifications.",
      "Bolus records with amounts, timestamps and imported classifications.",
    ],
    [
      "carbs",
      "Carb markers",
      "Markers identify recorded carb intake, including the amount at that time.",
      "Carb entries from an uploader or manual entry.",
    ],
    [
      "meter",
      "Meter checks",
      "These markers represent separate blood-glucose checks, rather than continuous sensor measurements.",
      "Recorded meter or calibration checks.",
    ],
    [
      "injection",
      "Basal injections",
      "A basal injection is an event with a unit amount. It is different from a pump's hourly basal rate.",
      "Long-acting insulin injection records.",
    ],
    [
      "device",
      "Device events",
      "Markers locate events such as sensor or infusion-site changes and other device records.",
      "Supported device-event records. Some sources do not upload all event types.",
    ],
    [
      "alarm",
      "Device alarms and warnings",
      "Markers show warnings or alarms recorded by a device. They are historical events, distinct from your Nocturne notification rules.",
      "Imported system events with severity and timestamps.",
    ],
    [
      "tracker",
      "Scheduled trackers",
      "Markers show scheduled tracker events in the timeline. Configure and start a tracker to make real events available.",
      "Tracker definitions and scheduled runs. Configure these in Tracker settings.",
    ],
    [
      "modes",
      "Pump modes",
      "Spans show when a pump was in a mode such as automatic, manual or suspended. A mode does not by itself specify insulin delivery.",
      "Imported pump mode spans.",
    ],
    [
      "profile",
      "Profile changes",
      "Spans identify which therapy profile was active over a period. Use the legend to reveal this extra track.",
      "Imported profile state spans and therapy profiles.",
    ],
    [
      "override",
      "Overrides",
      "Spans show temporary overrides, such as an exercise target. Use the legend to reveal them.",
      "Imported override state spans.",
    ],
    [
      "activity",
      "Activities",
      "Spans provide activity context, such as exercise or sleep. Use the legend to reveal this track.",
      "Supported activity spans from your source. Availability depends on the connector.",
    ],
  ].map(([id, title, meaning, source]) => ({
    ...common,
    id: `chart-${id}`,
    title,
    meaning,
    source,
    availability: source,
  }));
}
