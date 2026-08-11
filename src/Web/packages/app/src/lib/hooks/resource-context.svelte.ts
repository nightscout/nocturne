import { getContext, onDestroy, setContext } from "svelte";
import { SvelteMap } from "svelte/reactivity";
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
  /** Whether `current` is a retained value from a superseded query. */
  readonly stale: boolean;
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

/** One panel's fetch state, as reported to the layout's ResourceGuard. */
export interface ResourceRegistration {
  loading: boolean;
  error: Error | string | null | undefined;
  hasData: boolean;
  /** Loading a new value while a previous one is still on screen. */
  refreshing: boolean;
  errorTitle: string;
  refetch: () => void;
}

/**
 * Aggregate fetch state for a report page.
 *
 * Resources register under their own key and the page-level state is derived
 * across all of them. A page with two resources therefore surfaces either one's
 * failure and retries both; previously each registration overwrote the fields
 * unconditionally, so whichever wrote last decided what the page showed — on the
 * Sleep page a successful trends fetch masked a failed actogram fetch entirely.
 */
export class ResourceContext {
  #registrations = new SvelteMap<symbol, ResourceRegistration>();

  register(key: symbol, registration: ResourceRegistration): void {
    this.#registrations.set(key, registration);
  }

  unregister(key: symbol): void {
    this.#registrations.delete(key);
  }

  get #all(): ResourceRegistration[] {
    return [...this.#registrations.values()];
  }

  get #failed(): ResourceRegistration | undefined {
    return this.#all.find((r) => r.error != null);
  }

  /** Whether any registered resource is loading. */
  get loading(): boolean {
    return this.#all.some((r) => r.loading);
  }

  /** First error across registered resources. */
  get error(): Error | string | null | undefined {
    return this.#failed?.error ?? null;
  }

  /** Whether any resource has data (prevents skeleton flash). */
  get hasData(): boolean {
    return this.#all.some((r) => r.hasData);
  }

  /** Whether a new value is loading while a previous one is still on screen. */
  get refreshing(): boolean {
    return this.#all.some((r) => r.refreshing);
  }

  /** Title for the error card, taken from whichever resource failed. */
  get errorTitle(): string {
    return this.#failed?.errorTitle ?? this.#all[0]?.errorTitle ?? "Error Loading Data";
  }

  /** Retry every registered resource. */
  refetch = (): void => {
    for (const registration of this.#all) registration.refetch();
  };
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
 * Register `read` under a fresh key for the lifetime of the calling component.
 * Syncs in `$effect.pre` — before render — because the layout's ResourceGuard
 * conditionally renders children off this state.
 */
function registerWith(
  ctx: ResourceContext | undefined,
  read: () => ResourceRegistration
): void {
  if (!ctx) return;
  const key = Symbol("resource-registration");
  $effect.pre(() => ctx.register(key, read()));
  onDestroy(() => ctx.unregister(key));
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
  registerWith(
    getResourceContext(),
    () => ({
      loading: config.loading(),
      error: config.error(),
      hasData: config.hasData(),
      refreshing: config.loading() && config.hasData(),
      errorTitle: config.errorTitle ?? "Error Loading Data",
      refetch: config.refetch,
    })
  );
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

  // Create the query in a $derived tracking context so reactive queries
  // are never constructed inside an $effect (which would trigger a Svelte warning).
  const query = $derived(queryFn());

  // Hold on to the last resolved value. Changing the range creates a new query
  // whose `.current` is undefined, which failed both the page's own `{#if}` gate
  // and the layout's hasData check at once: the report went blank, then swapped in
  // a full-page skeleton, losing scroll position and every panel already valid.
  let retained = $state<T | undefined>(undefined);
  $effect.pre(() => {
    if (query.current !== undefined && query.current !== null) {
      retained = query.current;
    }
  });

  const current = $derived(query.current ?? retained);
  const stale = $derived(query.current === undefined && retained !== undefined);

  registerWith(
    getResourceContext(),
    () => ({
      loading: query.loading,
      error: query.error as Error | string | null | undefined,
      hasData: current !== undefined && current !== null,
      refreshing: query.loading && current !== undefined && current !== null,
      errorTitle,
      refetch: () => query.refresh(),
    })
  );

  const base = {
    get loading() {
      return query.loading;
    },
    get error() {
      return query.error;
    },
    get current() {
      return current;
    },
    get stale() {
      return stale;
    },
    refresh() {
      query.refresh();
    },
  };

  if (!dateParams) return base;

  // `date` is defined on `base` rather than built with `{ ...base, get date() }`:
  // spreading invokes each getter once and copies the results as plain values, so
  // the caller receives `current` frozen at undefined and `loading` frozen at true
  // for the life of the component. Every report that asked for `date` therefore
  // rendered its empty state forever while the layout's ResourceGuard — which reads
  // the query through its own closure — saw the data arrive and showed content.
  return Object.defineProperty(base, "date", {
    enumerable: true,
    get: (): DateInfo => ({
      from: dateParams.startDate,
      to: dateParams.endDate,
      dayCount: dateParams.dayCount,
    }),
  }) as ContextResourceWithDate<T>;
}
