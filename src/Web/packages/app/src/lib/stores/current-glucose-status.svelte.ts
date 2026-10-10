import { untrack } from "svelte";
import { getSummary } from "$api/generated/summaries.generated.remote";
import type { GlucoseStatus } from "$lib/api/generated/nocturne-api-client";
import { STALE_THRESHOLD_MS } from "$lib/constants/staleness";

/** Longest a superseded reading stays on screen while the summary catches up. */
export const PAIR_HOLD_MS = 5000;

/** Newest reading whose summary refresh failed; its predecessor is no longer held. */
let failedRefreshMills = $state<number | undefined>();

/**
 * Re-reads the shared summary once per new reading, so its server
 * classification follows the realtime store's newest entry. Call exactly once,
 * from the layout that creates the store: every reader of
 * {@link currentGlucoseStatus} sees the refreshed query.
 *
 * While `enabled`, the first load starts straight away rather than once the
 * store has a reading, so it runs alongside the store's own initial load
 * instead of a round-trip after it.
 */
export function refreshSummaryOnNewReading(
  currentMills: () => number | undefined,
  enabled: () => boolean
): void {
  $effect(() => {
    if (!enabled()) return;
    const mills = currentMills();
    const summary = untrack(() => getSummary());
    // Tracked so the effect runs once more when the first load lands: started before the
    // store had a reading, it may describe an older one than the store now shows.
    const firstLoadSettled = summary.ready;
    if (!mills) return;
    untrack(() => {
      // The first load reads the newest reading when it lands; refreshing it now would be a
      // second request. A later refresh in flight may predate this reading, so it does not count.
      const firstLoadInFlight = !firstLoadSettled && summary.loading;
      if (firstLoadInFlight || summary.current?.current?.mills === mills)
        return;
      // The tile falls back to neutral until a later reading retries; there is nothing to report.
      summary.refresh().catch(() => {
        failedRefreshMills = mills;
      });
    });
  });
}

/**
 * The server's status for the reading at `mills`. Undefined while the summary
 * still describes another reading, so a new value never wears the previous
 * one's colour.
 */
export function currentGlucoseStatus(
  mills: number | undefined
): GlucoseStatus | undefined {
  if (!mills) return undefined;
  const current = getSummary().current?.current;
  return current?.mills === mills ? current.status : undefined;
}

export interface DisplayedGlucose {
  readonly mills: number | undefined;
  readonly currentBG: number;
  readonly bgDelta: number;
  readonly direction: string;
  readonly status: GlucoseStatus | undefined;
}

interface GlucoseSource {
  readonly currentEntry: { readonly mills?: number } | null | undefined;
  readonly currentBG: number;
  readonly bgDelta: number;
  readonly direction: string;
}

/**
 * A reading's value, trend and server status as one unit, so a tile never shows
 * a new number in another reading's colour. While the summary refresh for a
 * newer reading is in flight the reading displayed just before it stays up, for
 * at most {@link PAIR_HOLD_MS} (a hidden tab throttles the timer, so the wall
 * clock is re-checked on visibilitychange and focus). A hold that expires or
 * whose refresh fails discards the pair for good, so an older reading can never
 * come back after the tile has moved on. Accepts an absent store. Call during
 * component initialisation.
 */
export function displayedGlucose(
  store: GlucoseSource | null | undefined
): DisplayedGlucose {
  let lastPaired = $state.raw<Omit<DisplayedGlucose, "status"> & {
    mills: number;
    status: GlucoseStatus;
  }>();
  let holdExpiredFor = $state<number | undefined>();
  let arrivedMills: number | undefined;
  let previousMills: number | undefined;
  let arrivedAt = 0;

  function expireIfElapsed() {
    if (
      arrivedMills !== undefined &&
      Date.now() - arrivedAt >= PAIR_HOLD_MS &&
      currentGlucoseStatus(arrivedMills) === undefined
    ) {
      lastPaired = undefined;
      holdExpiredFor = arrivedMills;
    }
  }

  // A hidden tab throttles the timer below, so the wall clock is re-checked when the tab returns.
  $effect(() => {
    const recheck = () => {
      if (document.visibilityState !== "hidden") expireIfElapsed();
    };
    document.addEventListener("visibilitychange", recheck);
    window.addEventListener("focus", recheck);
    return () => {
      document.removeEventListener("visibilitychange", recheck);
      window.removeEventListener("focus", recheck);
    };
  });

  $effect(() => {
    const mills = store?.currentEntry?.mills;
    if (!mills) return;
    if (mills !== arrivedMills) {
      previousMills = arrivedMills;
      arrivedMills = mills;
      arrivedAt = Date.now();
    }
    const status = currentGlucoseStatus(mills);
    if (status !== undefined) {
      if (failedRefreshMills === mills) failedRefreshMills = undefined;
      lastPaired = {
        mills,
        currentBG: store!.currentBG,
        bgDelta: store!.bgDelta,
        direction: store!.direction,
        status,
      };
      return;
    }
    if (failedRefreshMills === mills) {
      lastPaired = undefined;
      return;
    }
    const remaining = Math.max(0, PAIR_HOLD_MS - (Date.now() - arrivedAt));
    const timer = setTimeout(() => {
      lastPaired = undefined;
      holdExpiredFor = mills;
    }, remaining);
    return () => clearTimeout(timer);
  });

  // Reads tracked state only, so every derived sees value and status flip together at expiry.
  function held() {
    const mills = store?.currentEntry?.mills;
    if (!mills || !lastPaired || lastPaired.mills === mills) return undefined;
    // Until the effect has seen this reading, the last one it saw is the predecessor.
    const previous = mills === arrivedMills ? previousMills : arrivedMills;
    if (lastPaired.mills !== previous) return undefined;
    if (currentGlucoseStatus(mills) !== undefined) return undefined;
    if (holdExpiredFor === mills || failedRefreshMills === mills) return undefined;
    const gap = mills - lastPaired.mills;
    if (gap < 0 || gap > STALE_THRESHOLD_MS) return undefined;
    return lastPaired;
  }

  return {
    get mills() {
      return held()?.mills ?? store?.currentEntry?.mills;
    },
    get currentBG() {
      return held()?.currentBG ?? store?.currentBG ?? 0;
    },
    get bgDelta() {
      return held()?.bgDelta ?? store?.bgDelta ?? 0;
    },
    get direction() {
      return held()?.direction ?? store?.direction ?? "";
    },
    get status() {
      return held()?.status ?? currentGlucoseStatus(store?.currentEntry?.mills);
    },
  };
}
