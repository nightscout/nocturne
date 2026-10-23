import type { SequenceConfig } from "@nocturne/coach";
import type { LoopAnalysisPage } from "$lib/components/reports/loop-analysis/loop-analysis-pages.svelte";

export const sequences: SequenceConfig = {
  onboarding: {
    priority: 100,
    steps: [
      "onboarding.patient-details",
      "onboarding.devices",
      "onboarding.insulins",
      "onboarding.alerts",
      "onboarding.sharing",
      "onboarding.therapy-profile",
    ],
  },
  "dashboard-discovery": {
    priority: 75,
    prerequisite: "onboarding",
    steps: [
      "dashboard-discovery.chart-timerange",
      "dashboard-discovery.widgets",
      "dashboard-discovery.sidebar-glucose",
      "dashboard-discovery.reports",
    ],
  },
  "feature-intro": {
    priority: 50,
    prerequisite: "onboarding",
    steps: [
      "feature-intro.calendar-views",
      "feature-intro.calendar-trackers",
      "feature-intro.meals-attribution",
      "feature-intro.meals-matching",
      "feature-intro.food-quickpicks",
      "feature-intro.appearance-widgets",
    ],
  },
  "power-user": {
    priority: 25,
    prerequisite: "onboarding",
    steps: [
      "power-user.trackers",
      "power-user.connectors",
      "power-user.alert-rules",
      "power-user.compatibility",
    ],
  },
  "quick-tour": {
    priority: 200,
    steps: [
      "quick-tour.current-bg",
      "quick-tour.chart",
      "quick-tour.widgets",
      "quick-tour.sidebar",
    ],
  },
  "setup-invite": {
    priority: 200,
    steps: [
      "setup-invite.create-link",
      "setup-invite.roles",
      "setup-invite.copy-link",
    ],
    completesKeys: ["onboarding.sharing"],
  },
  "setup-alerts": {
    priority: 200,
    steps: [
      "setup-alerts.overview",
      "setup-alerts.wizard",
    ],
    completesKeys: ["onboarding.alerts"],
  },
  "setup-reports": {
    priority: 200,
    steps: [
      "setup-reports.metrics",
      "setup-reports.agp",
      "setup-reports.categories",
    ],
    completesKeys: ["dashboard-discovery.reports"],
  },
  "setup-connectors": {
    priority: 200,
    steps: [
      "setup-connectors.sources",
      "setup-connectors.server-connectors",
    ],
    completesKeys: ["power-user.connectors"],
  },
  // Outranks the page tours, so the shared header is explained once, on the first page seen. A
  // reader who dismisses it does not want the page's tour that would follow it either.
  "loop-analysis-pages": {
    priority: 65,
    steps: ["loop-analysis-pages.switcher"],
    quietOnDismiss: true,
  },
  "loop-analysis-daily-profile": {
    priority: 60,
    steps: [
      "loop-analysis-daily-profile.totals",
      "loop-analysis-daily-profile.glucose",
      "loop-analysis-daily-profile.correction",
      "loop-analysis-daily-profile.days",
    ],
  },
  "loop-analysis-meals-and-events": {
    priority: 60,
    steps: [
      "loop-analysis-meals-and-events.builder",
      "loop-analysis-meals-and-events.curves",
      "loop-analysis-meals-and-events.metrics",
      "loop-analysis-meals-and-events.events",
    ],
  },
  "loop-analysis-prediction-accuracy": {
    priority: 60,
    steps: [
      "loop-analysis-prediction-accuracy.horizons",
      "loop-analysis-prediction-accuracy.hours",
      "loop-analysis-prediction-accuracy.contexts",
    ],
  },
  "loop-analysis-loop-health": {
    priority: 60,
    steps: [
      "loop-analysis-loop-health.tiles",
      "loop-analysis-loop-health.hours",
      "loop-analysis-loop-health.gaps",
    ],
  },
  "loop-analysis-settings-evidence": {
    priority: 60,
    steps: [
      "loop-analysis-settings-evidence.observations",
      "loop-analysis-settings-evidence.chart",
      "loop-analysis-settings-evidence.blocks",
    ],
  },
};

/** The sequence that tours a Loop Analysis page, named after the page. */
export const loopAnalysisTour = (page: LoopAnalysisPage) => `loop-analysis-${page}`;

/** What a replay on a Loop Analysis page shows: the shared header, then the page's own tour. */
export const loopAnalysisReplay = (page: LoopAnalysisPage) => ["loop-analysis-pages", loopAnalysisTour(page)];
