import { getContext, setContext } from "svelte";
import { page } from "$app/state";
import type { LoopAnalysisResponse } from "#lib/api/index.js";
import { getEnvelope } from "#lib/api/generated/loopAnalysis.generated.remote.js";
import type { ReportsParamsReturn } from "#lib/hooks/date-params.svelte.js";
import { contextResource } from "#lib/hooks/resource-context.svelte.js";
import { dailyProfileQuery, readLoopAnalysisFilter } from "./loop-analysis-filter";
import { loopAnalysisPageAt, loopAnalysisPath, type LoopAnalysisPage } from "./loop-analysis-pages.svelte";

/**
 * What the five Loop Analysis pages share, owned by their layout so it outlives a move between
 * them: the filters, read from the URL so links and reloads carry them, the query they make with
 * the report range, and the envelope that query resolves to, fetched once for every page.
 */
export class LoopAnalysisState {
  readonly #range: ReportsParamsReturn;
  readonly #envelope: { readonly current: LoopAnalysisResponse | undefined; readonly stale: boolean };

  readonly filter = $derived(readLoopAnalysisFilter(page.url.searchParams));
  #lastQuery: ReturnType<typeof dailyProfileQuery> | undefined;
  /**
   * The request every page makes, over the report range's local days. A URL that asks for the same
   * keeps the same object, so a move between pages does not ask again.
   */
  readonly query = $derived.by(() => {
    const next = this.#request();
    if (this.#lastQuery && JSON.stringify(this.#lastQuery) === JSON.stringify(next)) return this.#lastQuery;
    return (this.#lastQuery = next);
  });
  /** The page on screen; undefined only on the address that redirects to one. */
  readonly current = $derived(loopAnalysisPageAt(page.url.pathname));

  constructor(range: ReportsParamsReturn) {
    this.#range = range;
    this.#envelope = contextResource(() => getEnvelope(this.query), { errorTitle: "Cannot load Loop analysis" });
  }

  #request() {
    return dailyProfileQuery(this.filter, { startDate: this.#range.fromDay, endDate: this.#range.toDay });
  }

  /** The last envelope, which answers an earlier filter while a newer one loads. */
  get envelope(): LoopAnalysisResponse | undefined {
    return this.#envelope.current;
  }

  /** The envelope that answers the filters on screen, or null while it loads. */
  get settled(): LoopAnalysisResponse | null {
    return this.#envelope.stale ? null : (this.#envelope.current ?? null);
  }

  get clamped(): boolean {
    return this.envelope?.historyClamped ?? page.data.limitTo24Hours ?? false;
  }

  /** The path of `target` under the filters on screen. */
  path(target: LoopAnalysisPage, hash = ""): string {
    return loopAnalysisPath(target, page.url.searchParams, hash);
  }
}

const LOOP_ANALYSIS_KEY = "nocturne:app:loop-analysis";

/** Call from the Loop Analysis layout. */
export function setLoopAnalysis(state: LoopAnalysisState): LoopAnalysisState {
  return setContext(LOOP_ANALYSIS_KEY, state);
}

export function getLoopAnalysis(): LoopAnalysisState {
  const state = getContext<LoopAnalysisState | undefined>(LOOP_ANALYSIS_KEY);
  if (!state) throw new Error("A Loop Analysis page renders inside the Loop Analysis layout.");
  return state;
}
