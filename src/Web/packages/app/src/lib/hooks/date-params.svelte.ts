/**
 * Centralized reports URL parameters using runed's useSearchParams.
 * This is the single source of truth for all report date range filtering.
 */
import { useSearchParams } from "runed/kit";
import { z } from "zod";
import { getLocalTimeZone, today, parseDate } from "@internationalized/date";
import { untrack } from "svelte";

/**
 * Zod schema for reports URL parameters.
 * - `days`: Number of days for relative range (e.g., "last 7 days")
 * - `from`/`to`: Explicit date range in YYYY-MM-DD format
 * - `isDefault`: Whether this is a report-set default (can be auto-adjusted on navigation)
 */
export const ReportsParamsSchema = z.object({
  days: z.coerce.number().optional(),
  from: z.string().optional(),
  to: z.string().optional(),
  isDefault: z.coerce.boolean().optional().default(true),
});

export type ReportsParams = z.infer<typeof ReportsParamsSchema>;

/**
 * Input type for remote functions - just the date range fields without isDefault.
 */
export type DateRangeInput = {
  days?: number;
  from?: string;
  to?: string;
};

/**
 * Create reactive reports URL parameters with auto-adjustment for report defaults.
 *
 * When `isDefault` is true in the URL and the report's `defaultDays` differs
 * from the current URL days, the URL is automatically updated to the report's default.
 *
 * @param defaultDays - The default number of days for this specific report (default: 7)
 * @returns Reactive params object with helper methods
 */
export function useDateParams(defaultDays = 7) {
  // showDefaults: true ensures all params are shown in URL, not just non-default ones
  // This is critical because runed by default omits params that match schema defaults
  const params = useSearchParams(ReportsParamsSchema, { showDefaults: true });
  let initialized = false;

  // Auto-adjust to report's default if current params are defaults and differ
  // Use $effect.pre with guards to prevent infinite update cycles
  $effect.pre(() => {
    if (initialized) return;

    // Read current params values
    const currentDays = params.days;
    const currentFrom = params.from;
    const currentTo = params.to;
    const isDefault = params.isDefault;

    if (isDefault && currentDays !== defaultDays && !currentFrom && !currentTo) {
      // This is a default that differs from this report's needs - adjust
      const endDate = today(getLocalTimeZone());
      const startDate = endDate.subtract({ days: defaultDays - 1 });
      initialized = true;

      // Use untrack to prevent this write from creating a dependency cycle
      untrack(() => {
        params.days = defaultDays;
        params.from = startDate.toString();
        params.to = endDate.toString();
        params.isDefault = true;
      });
    } else if (!currentDays && !currentFrom && !currentTo) {
      // No params at all - initialize with defaults
      const endDate = today(getLocalTimeZone());
      const startDate = endDate.subtract({ days: defaultDays - 1 });
      initialized = true;

      untrack(() => {
        params.days = defaultDays;
        params.from = startDate.toString();
        params.to = endDate.toString();
        params.isDefault = true;
      });
    } else {
      initialized = true;
    }
  });

  /**
   * Set a relative day range (e.g., "last 7 days").
   * Marks as default so it can be auto-adjusted when navigating to other reports.
   */
  function setDayRange(daysCount: number) {
    const endDate = today(getLocalTimeZone());
    const startDate = endDate.subtract({ days: daysCount - 1 });

    // Use direct property assignment for reliable URL updates
    params.days = daysCount;
    params.from = startDate.toString();
    params.to = endDate.toString();
    params.isDefault = true;
  }

  /**
   * Set an explicit custom date range.
   * Marks as NOT default so it's preserved when navigating to other reports.
   */
  function setCustomRange(from: string, to: string) {
    // Clear days first by setting directly, then update with full values
    // This ensures the URL properly reflects the custom range without the days param
    params.days = undefined;
    params.from = from;
    params.to = to;
    params.isDefault = false;
  }

  /**
   * Reset to this report's default day range.
   */
  function reset() {
    setDayRange(defaultDays);
  }

  /**
   * Get the date range input for remote functions.
   */
  function getDateRangeInput(): DateRangeInput {
    return {
      days: params.days,
      from: params.from,
      to: params.to,
    };
  }

  /**
   * Get the current date range as Date objects.
   */
  function getDateRange(): { start: Date; end: Date } {
    if (params.from && params.to) {
      try {
        const startParsed = parseDate(params.from);
        const endParsed = parseDate(params.to);
        return {
          start: startParsed.toDate(getLocalTimeZone()),
          end: endParsed.toDate(getLocalTimeZone()),
        };
      } catch {
        // Fall through to default
      }
    }

    // Calculate from days or use default
    const daysCount = params.days ?? defaultDays;
    const endDate = today(getLocalTimeZone());
    const startDate = endDate.subtract({ days: daysCount - 1 });

    return {
      start: startDate.toDate(getLocalTimeZone()),
      end: endDate.toDate(getLocalTimeZone()),
    };
  }

  return {
    // Reactive properties from runed
    get days() {
      return params.days;
    },
    get from() {
      return params.from;
    },
    get to() {
      return params.to;
    },
    get isDefault() {
      return params.isDefault;
    },

    // Helper methods
    setDayRange,
    setCustomRange,
    reset,
    getDateRangeInput,
    getDateRange,

    // Access to underlying params for advanced use
    update: params.update.bind(params),
    toURLSearchParams: params.toURLSearchParams.bind(params),
  };
}

export type ReportsParamsReturn = ReturnType<typeof useDateParams>;
