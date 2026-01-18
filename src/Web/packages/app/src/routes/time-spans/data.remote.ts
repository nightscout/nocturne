/**
 * Remote functions for Time Spans page data
 * Fetches all state span categories for visualization
 */
import { getRequestEvent, query } from "$app/server";
import { z } from "zod";
import { StateSpanCategory, type StateSpan, type Entry } from "$lib/api";

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
  /** Temp basal rate (U/hr) if applicable */
  rate?: number | null;
  /** Temp basal percent if applicable */
  percent?: number | null;
  /** Profile name if applicable */
  profileName?: string | null;
}

/**
 * Time spans page data response
 */
export interface TimeSpansPageData {
  pumpModeSpans: ProcessedSpan[];
  profileSpans: ProcessedSpan[];
  tempBasalSpans: ProcessedSpan[];
  overrideSpans: ProcessedSpan[];
  activitySpans: ProcessedSpan[];
  entries: Entry[];
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
 * Map temp basal state to CSS color variable
 */
function getTempBasalColor(_state: string): string {
  return "var(--insulin-basal)";
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
  const categoryColors: Record<string, string> = {
    [StateSpanCategory.Sleep]: "var(--pump-mode-sleep)",
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
    .map((span) => ({
      id: span.id ?? crypto.randomUUID(),
      category: span.category ?? StateSpanCategory.PumpMode,
      state: span.state ?? "Unknown",
      startTime: new Date(Math.max(span.startMills ?? 0, rangeStart)),
      endTime: new Date(Math.min(span.endMills ?? rangeEnd, rangeEnd)),
      color: colorFn(span.state ?? ""),
      metadata: span.metadata,
      // Extract temp basal rate/percent from metadata
      rate: (span.metadata?.rate as number) ?? (span.metadata?.absolute as number) ?? null,
      percent: (span.metadata?.percent as number) ?? null,
      // Extract profile name from metadata
      profileName: (span.metadata?.profileName as string) ?? (span.metadata?.name as string) ?? null,
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

    try {
      // Fetch all state span categories in parallel
      const [pumpModeSpans, profileSpans, tempBasalSpans, overrideSpans, activitySpans, entries] =
        await Promise.all([
          apiClient.stateSpans.getPumpModes(startTime, endTime),
          apiClient.stateSpans.getProfiles(startTime, endTime),
          apiClient.stateSpans.getTempBasals(startTime, endTime),
          apiClient.stateSpans.getOverrides(startTime, endTime),
          apiClient.stateSpans.getActivities(startTime, endTime),
          apiClient.entries.getEntries2(
            JSON.stringify({
              date: {
                $gte: startOfRange.toISOString(),
                $lte: endOfRange.toISOString(),
              },
            })
          ),
        ]);

      return {
        pumpModeSpans: processSpans(
          pumpModeSpans ?? [],
          startTime,
          endTime,
          getPumpModeColor
        ),
        profileSpans: processSpans(
          profileSpans ?? [],
          startTime,
          endTime,
          getProfileColor
        ),
        tempBasalSpans: processSpans(
          tempBasalSpans ?? [],
          startTime,
          endTime,
          getTempBasalColor
        ),
        overrideSpans: processSpans(
          overrideSpans ?? [],
          startTime,
          endTime,
          getOverrideColor
        ),
        activitySpans: (activitySpans ?? [])
          .filter((span) => {
            const spanStart = span.startMills ?? 0;
            const spanEnd = span.endMills ?? endTime;
            return spanEnd > startTime && spanStart < endTime;
          })
          .map((span) => ({
            id: span.id ?? crypto.randomUUID(),
            category: span.category ?? StateSpanCategory.Sleep,
            state: span.state ?? "Unknown",
            startTime: new Date(Math.max(span.startMills ?? 0, startTime)),
            endTime: new Date(Math.min(span.endMills ?? endTime, endTime)),
            color: getActivityColor(span.category ?? StateSpanCategory.Sleep),
            metadata: span.metadata,
            rate: null,
            percent: null,
            profileName: null,
          })),
        entries: Array.isArray(entries) ? entries : [],
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
        entries: [],
        dateRange: { from: startOfRange, to: endOfRange },
      };
    }
  }
);
