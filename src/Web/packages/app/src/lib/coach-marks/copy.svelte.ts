import type { CoachLabels, CoachMarkStep } from "@nocturne/coach";

/** Built per call so wuchale reads them in the current locale. */
export function coachCopy() {
  return {
    "quick-tour.current-bg": {
      title: "Your glucose, live",
      description:
        "This updates in real-time as new readings arrive from your CGM.",
    },
    "quick-tour.widgets": {
      title: "Customisable widgets",
      description:
        "Reorder or swap these in Settings \u2192 Appearance to show the stats you care about.",
    },
    "quick-tour.chart": {
      title: "Interactive chart",
      description:
        "Drag to pan, pinch or scroll to zoom. Select any point to see the exact reading and time.",
    },
    "feature-intro.calendar-views": {
      title: "View modes",
      description:
        "Switch between time in range and profile views to see different patterns.",
    },
    "feature-intro.calendar-trackers": {
      title: "Tracker events",
      description:
        "Tracker events appear on your calendar, colored by urgency.",
    },
    "feature-intro.meals-matching": {
      title: "Smart matching",
      description:
        "Nocturne suggests food matches based on your history. Review them here.",
    },
    "feature-intro.appearance-widgets": {
      title: "Widget order",
      description: "Drag to reorder your dashboard widgets.",
    },
    "loop-analysis-pages.switcher": {
      title: "Pages and filters",
      description:
        "Select a page name to open that page. Select Filters (the funnel icon) at the top of the report to choose the data that the report uses. All the Loop analysis pages use the same filters.",
    },
    "loop-analysis-daily-profile.totals": {
      title: "Daily totals",
      description:
        "These figures show the mean insulin for one day in the range. They show basal, automatic boluses, manual boluses and the net loop correction.",
    },
    "loop-analysis-daily-profile.glucose": {
      title: "Your usual day",
      description:
        "Each chart shows all the days in the range on one 24-hour axis. The line is the median. The bands show the spread of the days. Move the pointer along a chart to see the values at a time.",
    },
    "loop-analysis-daily-profile.correction": {
      title: "Net loop correction",
      description:
        "This chart shows the insulin that the loop added to your scheduled basal or removed from it. It includes temp basals and automatic boluses. Use the switches below the chart to show manual temp basals, suspends and inferred delivery.",
    },
    "loop-analysis-daily-profile.days": {
      title: "Days at a time of day",
      description:
        "Select a time on a chart to see the days for that time. Select a day to open it in Day in review. Faded areas have too few days to show a pattern.",
    },
    "loop-analysis-meals-and-events.builder": {
      title: "Events to align",
      description:
        "Select the type of event for each cohort, for example meals or correction boluses. You can compare a maximum of three cohorts. Isolated events only removes each event that has another meal, correction bolus or unannounced rise in its window.",
    },
    "loop-analysis-meals-and-events.curves": {
      title: "Before and after each event",
      description:
        "The charts align all the events of a cohort at 0. The dashed line at 0 is the time of the event. Each color is one cohort. The line is the median of its events.",
    },
    "loop-analysis-meals-and-events.metrics": {
      title: "Results after each event",
      description:
        "This table shows the median results for each cohort after its events, for example the peak rise and the time to the peak.",
    },
    "loop-analysis-meals-and-events.events": {
      title: "All the events",
      description:
        "This list shows each event in the cohorts. Select a column heading to sort the events. Select an event to open it in Day in review.",
    },
    "loop-analysis-prediction-accuracy.horizons": {
      title: "Error by horizon",
      description:
        "Each chart compares one prediction curve with the readings that followed it. The bold line is the bias. A positive bias means that the predictions were too high. If a horizon is marked Settings signal, only the bias at that horizon gives information about your settings.",
    },
    "loop-analysis-prediction-accuracy.hours": {
      title: "Bias by hour",
      description:
        "Each row is one hour of the day. Each column is one horizon. The color of a cell shows if the predictions from that hour were too high or too low. If there is more than one curve, select a curve at the top of By hour and context.",
    },
    "loop-analysis-prediction-accuracy.contexts": {
      title: "Bias by context",
      description:
        "This table divides the predictions into groups by the events in the three hours before them, for example a meal. The bias does not include interrupted predictions.",
    },
    "loop-analysis-loop-health.tiles": {
      title: "How the loop operated",
      description:
        "These figures show the percentage of time that the loop was closed and that the CGM and pump were active. They also show how the loop changed basal. They also show the limits that your settings put on the loop. When the data cannot give a figure, the tile shows Unknown and the reason.",
    },
    "loop-analysis-loop-health.hours": {
      title: "Each hour of the day",
      description:
        "Each chart shows a value for each hour of the day. Use these charts to find the hours when the loop stops or limits insulin. Select an hour to show only the gaps that started in that hour.",
    },
    "loop-analysis-loop-health.gaps": {
      title: "Gaps",
      description:
        "This list shows the gaps in the loop, with the longest gap first. Select a gap to open it in Day in review.",
    },
    "loop-analysis-settings-evidence.observations": {
      title: "Observations, not advice",
      description:
        "This page compares your profile settings with observations from your data. The figures are observations. They are not settings to change. Speak to your diabetes care team before you change a setting.",
    },
    "loop-analysis-settings-evidence.chart": {
      title: "Profile and observed values",
      description:
        "The chart shows your profile setting as a line. The dots show the observed values for each block of the day. Compare the two values for each block.",
    },
    "loop-analysis-settings-evidence.blocks": {
      title: "One row for each block",
      description:
        "Each row shows the observed value, the profile value, the number of days or events and the confidence. Select a row to see the days or events for that figure.",
    },
    "tracker-intro.pill": {
      title: "Tracker pill",
      description:
        "The pill shows the time since the last change. Its color changes when the tracker reaches a notification threshold. To change the order of the pills, go to Settings > Appearance.",
    },
    "tracker-intro.complete": {
      title: "Record a change",
      description:
        "When you change the item, select its pill. Then select Complete tracker. To start a new tracker from the change, select the Start another option in the Complete dialog. To change the lifespan and the alerts, go to Settings > Notifications & trackers.",
    },
    "tracker-intro.settings": {
      title: "Lifespan and thresholds",
      description:
        "Select Definitions. Then select the pencil icon of a tracker to edit it. The expected lifespan is the usual time between changes. Warning and Hazard alerts come from the notification thresholds.",
    },
    "tracker-intro.rail": {
      title: "Status pills",
      description:
        "Use this list to change the order of the status pills or to hide a pill. If you hid a legacy age from the dashboard, that age shows under Hidden legacy ages. Select Show to show the age on the dashboard again.",
    },
  } satisfies Record<string, CoachMarkStep>;
}

export type CoachCopyKey = keyof ReturnType<typeof coachCopy>;

export function coachFor(
  key: CoachCopyKey
): CoachMarkStep & { key: CoachCopyKey } {
  return { key, ...coachCopy()[key] };
}

/** The label of the button that replays the tour of the page on screen. */
export function tourReplayLabel(): string {
  return "Show the tour for this page";
}

/**
 * The popover's control text, built per call so wuchale reads it in the current
 * locale.
 */
export function coachLabels(): CoachLabels {
  return {
    dismiss: "Dismiss",
    showTip: "Show tip",
    back: "Back",
    next: "Next",
    gotIt: "Done",
    stepCounter: (current, total) => `${current} of ${total}`,
  };
}
