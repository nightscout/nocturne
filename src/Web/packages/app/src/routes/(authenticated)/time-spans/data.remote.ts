/**
 * Remote functions for Time Spans page data
 * Fetches all state span categories for visualization
 */
import { randomUUID } from "$lib/utils";
import { getRequestEvent, query } from "$app/server";
import { z } from "zod";
import {
  StateSpanCategory,
  type StateSpan,
  type ProfileActivationDto,
  type BasalDeliverySpanDto,
} from "$lib/api";
import { profileActivationLabel } from "$lib/utils/profile-activation-label";
import { readable } from "$lib/server/patient-timezone";

/**
 * Input schema for time spans data queries - supports date range
 */
const timeSpansSchema = z.object({
  from: z.string(), // ISO date string YYYY-MM-DD
  to: z.string(), // ISO date string YYYY-MM-DD
});

export type TimeSpansInput = z.infer<typeof timeSpansSchema>;

/**
 * Processed state span for chart rendering
 */
export interface ProcessedSpan {
  id: string;
  category: StateSpanCategory;
  state: string;
  startTime: Date;
  endTime: Date;
  color: string;
  metadata?: Record<string, unknown>;
  /** Profile name if applicable */
  profileName?: string | null;
}

/**
 * Time spans page data response
 */
export interface TimeSpansPageData {
  pumpModeSpans: ProcessedSpan[];
  profileSpans: ProcessedSpan[];
  tempBasalSpans: BasalDeliverySpanDto[];
  overrideSpans: ProcessedSpan[];
  activitySpans: ProcessedSpan[];
  dateRange: { from: Date; to: Date };
}

/**
 * Map pump mode state to CSS color variable
 */
function getPumpModeColor(state: string): string {
  const stateColors: Record<string, string> = {
    Automatic: "var(--pump-mode-automatic)",
    Limited: "var(--pump-mode-limited)",
    Manual: "var(--pump-mode-manual)",
    Boost: "var(--pump-mode-boost)",
    EaseOff: "var(--pump-mode-ease-off)",
    Sleep: "var(--pump-mode-sleep)",
    Exercise: "var(--pump-mode-exercise)",
    Suspended: "var(--pump-mode-suspended)",
    Off: "var(--pump-mode-off)",
    Liberty: "var(--pump-mode-liberty)",
  };
  return stateColors[state] ?? "var(--muted-foreground)";
}

/**
 * Map profile state to CSS color variable
 */
function getProfileColor(_state: string): string {
  // Profiles use a neutral color
  return "var(--chart-1)";
}

/**
 * Map override state to CSS color variable
 */
function getOverrideColor(state: string): string {
  const stateColors: Record<string, string> = {
    Boost: "var(--pump-mode-boost)",
    Exercise: "var(--pump-mode-exercise)",
    Sleep: "var(--pump-mode-sleep)",
    EaseOff: "var(--pump-mode-ease-off)",
  };
  return stateColors[state] ?? "var(--chart-2)";
}

/**
 * Map activity category to CSS color variable
 */
function getActivityColor(category: StateSpanCategory): string {
  const categoryColors: Partial<Record<StateSpanCategory, string>> = {
    [StateSpanCategory.Exercise]: "var(--pump-mode-exercise)",
    [StateSpanCategory.Illness]: "var(--system-event-warning)",
    [StateSpanCategory.Travel]: "var(--chart-3)",
  };
  return categoryColors[category] ?? "var(--muted-foreground)";
}

/**
 * Process raw state spans into chart-ready format
 */
function processSpans(
  spans: StateSpan[] | null | undefined,
  rangeStart: number,
  rangeEnd: number,
  colorFn: (state: string) => string
): ProcessedSpan[] {
  // Handle null/undefined/non-array input
  if (!spans || !Array.isArray(spans)) {
    return [];
  }

  return spans
    .filter((span) => {
      const spanStart = span.startMills ?? 0;
      const spanEnd = span.endMills ?? rangeEnd;
      return spanEnd > rangeStart && spanStart < rangeEnd;
    })
    .map((span) => {
      const profileName = span.metadata?.profileName ?? span.metadata?.name;
      return {
        id: span.id ?? randomUUID(),
        category: span.category ?? StateSpanCategory.PumpMode,
        state: span.state ?? "Unknown",
        startTime: new Date(Math.max(span.startMills ?? 0, rangeStart)),
        endTime: new Date(Math.min(span.endMills ?? rangeEnd, rangeEnd)),
        color: colorFn(span.state ?? ""),
        metadata: span.metadata,
        profileName: typeof profileName === "string" ? profileName : null,
      };
    });
}

/**
 * Process profile activations into chart-ready spans labelled by the profile they run
 */
function processProfileActivations(
  activations: ProfileActivationDto[] | null | undefined,
  rangeStart: number,
  rangeEnd: number
): ProcessedSpan[] {
  if (!activations || !Array.isArray(activations)) {
    return [];
  }

  return activations
    .filter((activation) => {
      const start = activation.startMills ?? 0;
      const end = activation.endMills ?? rangeEnd;
      return end > rangeStart && start < rangeEnd;
    })
    .map((activation) => ({
      id: activation.id ?? randomUUID(),
      category: StateSpanCategory.Profile,
      state: activation.phase ?? "Unknown",
      startTime: new Date(Math.max(activation.startMills ?? 0, rangeStart)),
      endTime: new Date(Math.min(activation.endMills ?? rangeEnd, rangeEnd)),
      color: getProfileColor(activation.phase ?? ""),
      profileName: profileActivationLabel(activation) ?? null,
    }));
}

/**
 * Get all state span data for time spans page visualization
 */
export const getTimeSpansData = query(
  timeSpansSchema,
  async ({ from, to }): Promise<TimeSpansPageData> => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    // Parse date range
    const fromDate = new Date(from);
    const toDate = new Date(to);

    // Create start of first day and end of last day
    const startOfRange = new Date(
      fromDate.getFullYear(),
      fromDate.getMonth(),
      fromDate.getDate(),
      0,
      0,
      0
    );
    const endOfRange = new Date(
      toDate.getFullYear(),
      toDate.getMonth(),
      toDate.getDate(),
      23,
      59,
      59
    );
    const startTime = startOfRange.getTime();
    const endTime = endOfRange.getTime();
    const rangeFrom = startOfRange.toISOString();
    const rangeTo = endOfRange.toISOString();

    try {
      // Each category has its own read scope, so a lane the caller may not
      // read is left empty rather than emptying the others.
      const [pumpModeRes, profileRes, tempBasalRes, overrideRes, activityRes] =
        await Promise.all([
          readable(() => apiClient.stateSpans.getPumpModes(rangeFrom, rangeTo)),
          readable(() =>
            apiClient.profileActivations.getActivations(rangeFrom, rangeTo)
          ),
          readable(() =>
            apiClient.chartData.getBasalDeliverySpans(startTime, endTime)
          ),
          readable(() => apiClient.stateSpans.getOverrides(rangeFrom, rangeTo)),
          readable(() => apiClient.stateSpans.getActivities(rangeFrom, rangeTo)),
        ]);

      const pumpModeSpans = pumpModeRes?.data ?? [];
      const profileActivations = profileRes ?? [];
      const tempBasalSpans = tempBasalRes ?? [];
      const overrideSpans = overrideRes?.data ?? [];
      const activitySpans = activityRes?.data ?? [];

      return {
        pumpModeSpans: processSpans(
          pumpModeSpans,
          startTime,
          endTime,
          getPumpModeColor
        ),
        profileSpans: processProfileActivations(
          profileActivations,
          startTime,
          endTime
        ),
        tempBasalSpans,
        overrideSpans: processSpans(
          overrideSpans,
          startTime,
          endTime,
          getOverrideColor
        ),
        activitySpans: activitySpans
          .filter((span: StateSpan) => {
            const spanStart = span.startMills ?? 0;
            const spanEnd = span.endMills ?? endTime;
            return spanEnd > startTime && spanStart < endTime;
          })
          .map((span: StateSpan) => ({
            id: span.id ?? randomUUID(),
            category: span.category ?? StateSpanCategory.Exercise,
            state: span.state ?? "Unknown",
            startTime: new Date(Math.max(span.startMills ?? 0, startTime)),
            endTime: new Date(Math.min(span.endMills ?? endTime, endTime)),
            color: getActivityColor(span.category ?? StateSpanCategory.Exercise),
            metadata: span.metadata,
            profileName: null,
          })),
        dateRange: { from: startOfRange, to: endOfRange },
      };
    } catch (err) {
      console.error("Error loading time spans data:", err);
      // Return empty data instead of throwing for unauthenticated users
      return {
        pumpModeSpans: [],
        profileSpans: [],
        tempBasalSpans: [],
        overrideSpans: [],
        activitySpans: [],
        dateRange: { from: startOfRange, to: endOfRange },
      };
    }
  }
);
