// A .svelte.ts module so wuchale extracts its strings.
import { getContext, setContext } from "svelte";
import {
  NotificationUrgency,
  type ClockElement,
  type ClockFaceConfig,
  type ClockFaceData,
  type ClockPartAccessDto,
  type ClockTrackerDto,
} from "@nocturne/api-client";
import { scopeLabels } from "#lib/constants/scope-labels.svelte.js";
import type { PredictionData, TransformedChartData } from "@nocturne/charts/core";

/**
 * What a clock face shows beyond the latest reading, as the API read it for the face: the public
 * clock polls it by the face's id, the editor previews it for the layout being edited. Null until
 * the first read, so an element shows nothing rather than a stand-in value.
 */
export interface ClockFaceDataSource {
  readonly data: ClockFaceData | null;
  readonly chart: TransformedChartData | null;
  readonly predictions: PredictionData | null;
}

const CLOCK_FACE_DATA_KEY = "nocturne:app:clock-face-data";

const NO_DATA: ClockFaceDataSource = { data: null, chart: null, predictions: null };

export function setClockFaceDataSource(source: ClockFaceDataSource): void {
  setContext(CLOCK_FACE_DATA_KEY, source);
}

/** The provided source, or one with nothing in it. */
export function getClockFaceDataSource(): ClockFaceDataSource {
  return getContext<ClockFaceDataSource>(CLOCK_FACE_DATA_KEY) ?? NO_DATA;
}

/** The hours a summary or chart element reads when it names none, as the API defaults it. */
export const DEFAULT_ELEMENT_HOURS = 3;

/** The minutes ahead a forecast element reads when it names none, as the API defaults it. */
export const DEFAULT_MINUTES_AHEAD = 30;

/** A summary element's time in range, as the face shows it. */
export function inRangeLabel(percent: number): string {
  return `${Math.round(percent)}% in range`;
}

const URGENCY_RANK: Record<NotificationUrgency, number> = {
  [NotificationUrgency.Info]: 1,
  [NotificationUrgency.Warn]: 2,
  [NotificationUrgency.Hazard]: 3,
  [NotificationUrgency.Urgent]: 4,
};

const THRESHOLD_RANK: Record<string, number> = {
  always: 0,
  info: 1,
  warn: 2,
  hazard: 3,
  urgent: 4,
};

/**
 * The trackers a tracker element shows: the one it names, or every one in its categories (all of
 * them when it names none), at or above its visibility threshold.
 */
export function visibleTrackers(
  element: ClockElement,
  data: ClockFaceData | null
): ClockTrackerDto[] {
  const threshold = THRESHOLD_RANK[element.visibilityThreshold ?? "always"] ?? 0;
  const categories = element.categories ?? [];
  return (data?.trackers ?? []).filter((tracker) => {
    if (element.type === "tracker" && tracker.definitionId !== element.definitionId) return false;
    if (
      element.type === "trackers" &&
      categories.length > 0 &&
      !categories.includes(tracker.category ?? "")
    )
      return false;
    return (tracker.urgency ? URGENCY_RANK[tracker.urgency] : 0) >= threshold;
  });
}

/** Element types whose values the face data endpoints read. */
const DATA_ELEMENT_TYPES: ReadonlySet<string> = new Set([
  "iob",
  "cob",
  "basal",
  "forecast",
  "summary",
  "tracker",
  "trackers",
]);

function elementsOf(config: ClockFaceConfig | undefined): ClockElement[] {
  return (config?.rows ?? []).flatMap((row) => row.elements ?? []);
}

/** Whether the face has an element the data endpoint serves. */
export function faceReadsData(config: ClockFaceConfig | undefined): boolean {
  return elementsOf(config).some((e) => DATA_ELEMENT_TYPES.has(e.type ?? ""));
}

/** Whether the face has a chart element. */
export function faceDrawsChart(config: ClockFaceConfig | undefined): boolean {
  return elementsOf(config).some((e) => e.type === "chart");
}

/**
 * Only what decides the values a layout reads, so the editor's preview query keeps its key while
 * the face is restyled or moved around.
 */
export function dataShapeOf(config: ClockFaceConfig): ClockFaceConfig {
  return {
    rows: [
      {
        elements: elementsOf(config)
          .filter((e) => DATA_ELEMENT_TYPES.has(e.type ?? ""))
          .map((e) => ({
            type: e.type,
            hours: e.hours ?? undefined,
            minutesAhead: e.minutesAhead ?? undefined,
            definitionId: e.definitionId || undefined,
          })),
      },
    ],
  };
}

/**
 * The scope the caller lacks to add a part, as the API reports it; null when the caller may add it
 * or the API has not answered yet. A chart's base is `feature` undefined.
 */
export function missingScopeFor(
  parts: readonly ClockPartAccessDto[],
  type: string,
  feature?: string
): string | null {
  return (
    parts.find((p) => p.type === type && (p.feature ?? undefined) === feature)?.missingScope ?? null
  );
}

/** Why the caller cannot add a part, for the editor to show beside it. */
export function missingScopeReason(scope: string): string {
  const labels: Record<string, string | undefined> = scopeLabels();
  const label = labels[scope] ?? scope;
  return `You need the "${label}" permission.`;
}
