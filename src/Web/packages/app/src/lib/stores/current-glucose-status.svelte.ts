import { untrack } from "svelte";
import { getSummary } from "$api/generated/summaries.generated.remote";
import type { GlucoseStatus } from "$lib/api/generated/nocturne-api-client";

/**
 * Re-reads the shared summary once per new reading, so its server
 * classification follows the realtime store's newest entry. Call exactly once,
 * from the layout that creates the store: every reader of
 * {@link currentGlucoseStatus} sees the refreshed query.
 */
export function refreshSummaryOnNewReading(
  currentMills: () => number | undefined
): void {
  $effect(() => {
    const mills = currentMills();
    if (!mills) return;
    untrack(() => {
      const summary = getSummary();
      // The first load already reads the newest reading; refreshing it would be a second
      // request. A later refresh in flight may predate this reading, so it does not count.
      const firstLoadInFlight = !summary.ready && summary.loading;
      if (firstLoadInFlight || summary.current?.current?.mills === mills)
        return;
      // The tile stays neutral until a later reading retries; there is nothing to report.
      summary.refresh().catch(() => {});
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
