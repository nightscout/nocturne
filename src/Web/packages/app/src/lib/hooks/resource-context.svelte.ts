import { getContext, setContext } from "svelte";
import type { ReportsParamsReturn } from "./date-params.svelte";

export interface DateInfo {
  readonly from: Date;
  readonly to: Date;
  readonly dayCount: number;
}

type QueryResult<T> = { loading: boolean; error: unknown; current: T | undefined; refresh: () => void };

interface ContextResourceBase<T> {
  readonly loading: boolean;
  readonly error: unknown;
  readonly current: T | undefined;
  refresh(): void;
}

interface ContextResourceWithDate<T> extends ContextResourceBase<T> {
  readonly date: DateInfo;
}

interface ContextResourceOptions {
  errorTitle?: string;
}

interface ContextResourceOptionsWithDate extends ContextResourceOptions {
  dateParams: ReportsParamsReturn;
}

const RESOURCE_CONTEXT_KEY = Symbol("resource-context");

export interface ResourceState {
  /** Whether any registered resource is loading */
  loading: boolean;
  /** First error from any registered resource */
  error: Error | string | null | undefined;
  /** Whether any resource has data (prevents skeleton flash) */
  hasData: boolean;
  /** Title for the error card */
  errorTitle: string;
  /** Function to retry all registered resources */
  refetch: () => void;
}

/**
 * Reactive resource context class using Svelte 5 runes.
 * Using a class with $state ensures getters are properly reactive.
 */
export class ResourceContext {
  loading = $state(false);
  error = $state<Error | string | null | undefined>(null);
  hasData = $state(false);
  errorTitle = $state("Error Loading Data");
  refetch = $state<() => void>(() => {});

  setResource(newState: Partial<ResourceState>) {
    if (newState.loading !== undefined) this.loading = newState.loading;
    if (newState.error !== undefined) this.error = newState.error;
    if (newState.hasData !== undefined) this.hasData = newState.hasData;
    if (newState.errorTitle !== undefined) this.errorTitle = newState.errorTitle;
    if (newState.refetch !== undefined) this.refetch = newState.refetch;
  }
}

/**
 * Creates and sets the resource context.
 * Call this from the layout component.
 */
export function createResourceContext(): ResourceContext {
  const context = new ResourceContext();
  setContext(RESOURCE_CONTEXT_KEY, context);
  return context;
}

/**
 * Gets the resource context.
 * Call this from pages to register their resource state.
 */
export function getResourceContext(): ResourceContext | undefined {
  return getContext<ResourceContext | undefined>(RESOURCE_CONTEXT_KEY);
}

/**
 * Registers a resource's state with the context.
 * Call this from pages to integrate with layout-level ResourceGuard.
 *
 * @example
 * ```svelte
 * <script>
 *   import { useResourceContext } from "$lib/hooks/resource-context.svelte";
 *   import { resource } from "runed";
 *
 *   const myResource = resource(...);
 *
 *   // Register with context for layout-level loading/error handling
 *   useResourceContext({
 *     loading: () => myResource.loading,
 *     error: () => myResource.error,
 *     hasData: () => !!myResource.current,
 *     errorTitle: "Error Loading My Data",
 *     refetch: () => myResource.refetch(),
 *   });
 * </script>
 * ```
 */
export function useResourceContext(config: {
  loading: () => boolean;
  error: () => Error | string | null | undefined;
  hasData: () => boolean;
  errorTitle?: string;
  refetch: () => void;
}): void {
  const ctx = getResourceContext();
  if (!ctx) return;

  // Use an effect to keep context state synced with resource state
  $effect(() => {
    ctx.setResource({
      loading: config.loading(),
      error: config.error(),
      hasData: config.hasData(),
      errorTitle: config.errorTitle ?? "Error Loading Data",
      refetch: config.refetch,
    });
  });
}

/**
 * A wrapper that takes a SvelteKit query and automatically registers with the layout's ResourceGuard.
 *
 * This is the recommended way to use queries in report pages - it handles:
 * - Automatic registration with layout-level loading/error handling
 * - Uses $effect.pre to sync state before render
 * - Optionally exposes date info from URL params via the `date` property
 *
 * @example
 * ```svelte
 * <script>
 *   import { contextResource } from "$lib/hooks/resource-context.svelte";
 *   import { getReportsData } from "$api/reports.remote";
 *   import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
 *
 *   const reportsParams = requireDateParamsContext(14);
 *
 *   const reportsQuery = contextResource(
 *     () => getReportsData(reportsParams.dateRangeInput),
 *     { errorTitle: "Error Loading AGP Report", dateParams: reportsParams }
 *   );
 *
 *   // Date info derived from URL params — no separate $derived needed
 *   // reportsQuery.date.from, reportsQuery.date.to, reportsQuery.date.dayCount
 * </script>
 * ```
 */
export function contextResource<T>(
  queryFn: () => QueryResult<T>,
  options: ContextResourceOptionsWithDate
): ContextResourceWithDate<T>;
export function contextResource<T>(
  queryFn: () => QueryResult<T>,
  options?: ContextResourceOptions
): ContextResourceBase<T>;
export function contextResource<T>(
  queryFn: () => QueryResult<T>,
  options: ContextResourceOptions & { dateParams?: ReportsParamsReturn } = {}
): ContextResourceBase<T> | ContextResourceWithDate<T> {
  const { errorTitle = "Error Loading Data", dateParams } = options;
  const ctx = getResourceContext();

  // Use $effect.pre to sync state BEFORE render
  $effect.pre(() => {
    if (ctx) {
      const query = queryFn();
      ctx.loading = query.loading;
      ctx.error = query.error as Error | string | null | undefined;
      ctx.hasData = query.current !== undefined && query.current !== null;
      ctx.errorTitle = errorTitle;
      ctx.refetch = () => query.refresh();
    }
  });

  const base = {
    get loading() {
      return queryFn().loading;
    },
    get error() {
      return queryFn().error;
    },
    get current() {
      return queryFn().current;
    },
    refresh() {
      queryFn().refresh();
    },
  };

  if (!dateParams) return base;

  return {
    ...base,
    get date(): DateInfo {
      const range = dateParams.getDateRange();
      const ms = range.end.getTime() - range.start.getTime();
      return {
        from: range.start,
        to: range.end,
        dayCount: Math.max(1, Math.round(ms / (1000 * 60 * 60 * 24))),
      };
    },
  };
}
