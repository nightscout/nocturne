import type { SequenceDefinition } from "@nocturne/coach";
import type { LoopAnalysisPage } from "#lib/components/reports/loop-analysis/loop-analysis-pages.svelte.js";

/** Set by the authenticated layout once the tenant has finished the onboarding core. */
export const ONBOARDING_CORE_GATE = "onboarding-core";

/**
 * Who reads the app shell: an account holder, a guest link session, or the anonymous viewer of a
 * public share. Each tour is written for one of them and shown to no other.
 */
export type CoachAudience = "member" | "guest" | "share";

/** Inside the authenticated route group a reader with no user is the public share view. */
export function coachAudience(viewer: { user?: unknown; isGuestSession?: boolean }): CoachAudience {
  if (viewer.isGuestSession) return "guest";
  return viewer.user ? "member" : "share";
}

export const sequences: Record<string, SequenceDefinition & { audience: CoachAudience }> = {
  "feature-intro": {
    audience: "member",
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
    audience: "member",
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
    audience: "member",
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
    audience: "member",
    priority: 65,
    steps: ["loop-analysis-pages.switcher"],
    quietOnDismiss: true,
  },
  "loop-analysis-daily-profile": {
    audience: "member",
    priority: 60,
    steps: [
      "loop-analysis-daily-profile.totals",
      "loop-analysis-daily-profile.glucose",
      "loop-analysis-daily-profile.correction",
      "loop-analysis-daily-profile.days",
    ],
  },
  "loop-analysis-meals-and-events": {
    audience: "member",
    priority: 60,
    steps: [
      "loop-analysis-meals-and-events.builder",
      "loop-analysis-meals-and-events.curves",
      "loop-analysis-meals-and-events.metrics",
      "loop-analysis-meals-and-events.events",
    ],
  },
  "loop-analysis-prediction-accuracy": {
    audience: "member",
    priority: 60,
    steps: [
      "loop-analysis-prediction-accuracy.horizons",
      "loop-analysis-prediction-accuracy.hours",
      "loop-analysis-prediction-accuracy.contexts",
    ],
  },
  "loop-analysis-loop-health": {
    audience: "member",
    priority: 60,
    steps: [
      "loop-analysis-loop-health.tiles",
      "loop-analysis-loop-health.hours",
      "loop-analysis-loop-health.gaps",
    ],
  },
  "loop-analysis-settings-evidence": {
    audience: "member",
    priority: 60,
    steps: [
      "loop-analysis-settings-evidence.observations",
      "loop-analysis-settings-evidence.chart",
      "loop-analysis-settings-evidence.blocks",
    ],
  },
  // A guest link is read-only and ends after 48 hours, and its reader came for the data, not the
  // app: the tour says what the access is and where the long-period summaries are.
  "guest-tour": {
    audience: "guest",
    priority: 200,
    steps: ["guest-tour.access", "guest-tour.chart", "guest-tour.reports"],
  },
  // A public share has no account behind it and may be opened by anyone: the tour says only what
  // the page shows.
  "share-tour": {
    audience: "share",
    priority: 200,
    steps: ["share-tour.reading", "share-tour.chart"],
  },
};

/** The sequence that tours a Loop Analysis page, named after the page. */
export const loopAnalysisTour = (page: LoopAnalysisPage) => `loop-analysis-${page}`;

/** What a replay on a Loop Analysis page shows: the shared header, then the page's own tour. */
export const loopAnalysisReplay = (page: LoopAnalysisPage) => ["loop-analysis-pages", loopAnalysisTour(page)];
