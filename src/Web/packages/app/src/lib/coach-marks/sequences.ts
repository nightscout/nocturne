import type { SequenceConfig } from "@nocturne/coach";
import type { LoopAnalysisPage } from "$lib/components/reports/loop-analysis/loop-analysis-pages.svelte";

/** Set by the authenticated layout once the tenant has finished the onboarding core. */
export const ONBOARDING_CORE_GATE = "onboarding-core";

export const sequences: SequenceConfig = {
  "feature-intro": {
    priority: 50,
    prerequisite: ONBOARDING_CORE_GATE,
    steps: [
      "feature-intro.calendar-views",
      "feature-intro.calendar-trackers",
      "feature-intro.meals-matching",
      "feature-intro.appearance-widgets",
    ],
  },
  "quick-tour": {
    priority: 200,
    prerequisite: ONBOARDING_CORE_GATE,
    steps: [
      "quick-tour.current-bg",
      "quick-tour.chart",
      "quick-tour.widgets",
    ],
  },
  // Started by the legacy age conversion dialog once it closes, never organically: shown on its own,
  // it would open over that dialog as the new pill mounts, and show its settings steps to users with
  // no trackers. It has one chance: a reload or another started tour drops a run still waiting.
  "tracker-intro": {
    priority: 40,
    startOnly: true,
    steps: [
      "tracker-intro.pill",
      "tracker-intro.complete",
      "tracker-intro.settings",
      "tracker-intro.rail",
    ],
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
