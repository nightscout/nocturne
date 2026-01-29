/**
 * Centralized reports URL parameters using runed's useSearchParams.
 * This is the single source of truth for all report date range filtering.
 *
 * IMPORTANT: This hook is designed to avoid infinite update cycles by:
 * 1. Using stable string representations for date range input (not new objects)
 * 2. Using $state.snapshot for memoization
 * 3. Carefully guarding effect execution with initialization flags
 *
 * USAGE: In reports, use context-based sharing via:
 * - `setDateParamsContext(params)` in the reports layout to provide the shared instance
 * - `getDateParamsContext()` in child components to consume the shared instance
 */
import { useSearchParams } from "runed/kit";
import { z } from "zod";
import { getLocalTimeZone, today, parseDate } from "@internationalized/date";
import { getContext, setContext, untrack } from "svelte";

/**
 * Zod schema for reports URL parameters.
 * - `days`: Number of days for relative range (e.g., "last 7 days")
 * - `from`/`to`: Explicit date range in YYYY-MM-DD format
 * - `isDefault`: Whether this is a report-set default (can be auto-adjusted on navigation)
 *
 * IMPORTANT: All fields must use `.nullable().default(null)` to ensure runed
 * can detect all schema keys. Without explicit defaults, Zod omits undefined
 * fields from validation results, causing runed's `has()` check to fail and
 * preventing URL updates when setting values.
 */
export const ReportsParamsSchema = z.object({
  days: z.coerce.number().nullable().default(null),
  from: z.string().nullable().default(null),
  to: z.string().nullable().default(null),
  isDefault: z.coerce.boolean().nullable().default(true),
});

export type ReportsParams = z.infer<typeof ReportsParamsSchema>;

/**
 * Input type for remote functions - just the date range fields without isDefault.
 * Uses null | undefined to match both the schema (nullable) and common JS patterns.
 */
export type DateRangeInput = {
  days?: number | null;
  from?: string | null;
  to?: string | null;
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

  // Track initialization to prevent infinite loops
  let initialized = $state(false);

  // Stable memoized date range input - only changes when actual values change
  // This prevents downstream $derived statements from recreating queries
  let memoizedInput = $state<DateRangeInput>({
    days: undefined,
    from: undefined,
    to: undefined,
  });

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

      // Use untrack to prevent this write from creating a dependency cycle
      untrack(() => {
        params.days = defaultDays;
        params.from = startDate.toString();
        params.to = endDate.toString();
        params.isDefault = true;
        initialized = true;

        // Update memoized input
        memoizedInput = {
          days: defaultDays,
          from: startDate.toString(),
          to: endDate.toString(),
        };
      });
    } else if (!currentDays && !currentFrom && !currentTo) {
      // No params at all - initialize with defaults
      const endDate = today(getLocalTimeZone());
      const startDate = endDate.subtract({ days: defaultDays - 1 });

      untrack(() => {
        params.days = defaultDays;
        params.from = startDate.toString();
        params.to = endDate.toString();
        params.isDefault = true;
        initialized = true;

        // Update memoized input
        memoizedInput = {
          days: defaultDays,
          from: startDate.toString(),
          to: endDate.toString(),
        };
      });
    } else {
      // Params already set - just mark as initialized and sync memoized input
      initialized = true;
      memoizedInput = {
        days: currentDays,
        from: currentFrom,
        to: currentTo,
      };
    }
  });

  // Sync memoized input when params change AFTER initialization
  // This is a separate effect to avoid the initialization logic
  $effect(() => {
    if (!initialized) return;

    const currentDays = params.days;
    const currentFrom = params.from;
    const currentTo = params.to;

    // Only update if values actually changed (compare primitives, not objects)
    if (
      memoizedInput.days !== currentDays ||
      memoizedInput.from !== currentFrom ||
      memoizedInput.to !== currentTo
    ) {
      memoizedInput = {
        days: currentDays,
        from: currentFrom,
        to: currentTo,
      };
    }
  });

  /**
   * Set a relative day range (e.g., "last 7 days").
   * Marks as NOT default since this is an explicit user selection that should
   * be preserved when navigating between reports.
   */
  function setDayRange(daysCount: number) {
    const endDate = today(getLocalTimeZone());
    const startDate = endDate.subtract({ days: daysCount - 1 });

    // Use direct property assignment for reliable URL updates
    params.days = daysCount;
    params.from = startDate.toString();
    params.to = endDate.toString();
    params.isDefault = false;
  }

  /**
   * Set an explicit custom date range.
   * Marks as NOT default so it's preserved when navigating to other reports.
   */
  function setCustomRange(from: string, to: string) {
    // Clear days first by setting directly, then update with full values
    // This ensures the URL properly reflects the custom range without the days param
    params.days = null;
    params.from = from;
    params.to = to;
    params.isDefault = false;
  }

  /**
   * Reset to this report's default day range.
   * This marks as default again so it can be auto-adjusted when navigating to other reports.
   */
  function reset() {
    const endDate = today(getLocalTimeZone());
    const startDate = endDate.subtract({ days: defaultDays - 1 });

    params.days = defaultDays;
    params.from = startDate.toString();
    params.to = endDate.toString();
    params.isDefault = true;
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

    // STABLE date range input - use this for queries to avoid infinite loops
    // This is a getter that returns the memoized $state, not a new object
    get dateRangeInput(): DateRangeInput {
      return memoizedInput;
    },

    // Helper methods
    setDayRange,
    setCustomRange,
    reset,
    getDateRange,

    /**
     * Internal method: Set day range while keeping isDefault=true.
     * Used when auto-adjusting to a report's preferred default.
     */
    _setDefaultDayRange(daysCount: number) {
      const endDate = today(getLocalTimeZone());
      const startDate = endDate.subtract({ days: daysCount - 1 });
      params.days = daysCount;
      params.from = startDate.toString();
      params.to = endDate.toString();
      params.isDefault = true;
    },

    /**
     * Get the date range input for remote functions.
     * Returns the memoized state for stability (prevents infinite update loops).
     * @deprecated Use the `dateRangeInput` getter property instead for cleaner syntax
     */
    getDateRangeInput(): DateRangeInput {
      return memoizedInput;
    },

    // Access to underlying params for advanced use
    update: params.update.bind(params),
    toURLSearchParams: params.toURLSearchParams.bind(params),
  };
}

export type ReportsParamsReturn = ReturnType<typeof useDateParams>;

/**
 * Context key for shared date params instance.
 */
const DATE_PARAMS_CONTEXT_KEY = Symbol("date-params");

/**
 * Set the date params instance in Svelte context.
 * Call this in the reports layout to provide a shared instance to all children.
 *
 * @param params - The useDateParams instance to share
 */
export function setDateParamsContext(params: ReportsParamsReturn): void {
  setContext(DATE_PARAMS_CONTEXT_KEY, params);
}

/**
 * Get the shared date params instance from Svelte context.
 * Use this in report pages and the filter sidebar to access the shared instance.
 *
 * @returns The shared useDateParams instance, or undefined if not in context
 */
export function getDateParamsContext(): ReportsParamsReturn | undefined {
  return getContext<ReportsParamsReturn | undefined>(DATE_PARAMS_CONTEXT_KEY);
}

/**
 * Get the shared date params from context, throwing if not available.
 * Use this when you're certain the context has been set (e.g., in report pages).
 *
 * If `reportDefaultDays` is provided and the current params are in default mode
 * (isDefault=true), this will automatically adjust the date range to match
 * the report's preferred default. This allows different reports to have
 * different sensible defaults while preserving user selections.
 *
 * @param reportDefaultDays - Optional: The ideal default day range for this specific report.
 *                            If provided and params are in default mode, will adjust to this range.
 * @returns The shared useDateParams instance
 * @throws Error if context is not set
 */
export function requireDateParamsContext(reportDefaultDays?: number): ReportsParamsReturn {
  const params = getDateParamsContext();
  if (!params) {
    throw new Error(
      "Date params context not found. Ensure setDateParamsContext is called in a parent component."
    );
  }

  // Auto-adjust if this report has a different default and we're in default mode
  if (reportDefaultDays !== undefined && params.isDefault && params.days !== reportDefaultDays) {
    // The user hasn't made a custom selection (isDefault=true), so we can adjust
    // to this report's preferred default range using direct property assignment
    // (which triggers runed's reactive proxy correctly)
    params._setDefaultDayRange(reportDefaultDays);
  }

  return params;
}
