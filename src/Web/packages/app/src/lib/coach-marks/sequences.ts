import type { SequenceConfig } from "@nocturne/coach";

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
    steps: [
      "quick-tour.current-bg",
      "quick-tour.chart",
      "quick-tour.widgets",
    ],
  },
};
