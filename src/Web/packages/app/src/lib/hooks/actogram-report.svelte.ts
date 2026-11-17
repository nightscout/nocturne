import { getActogramRange } from "#lib/api/actogram.remote.js";
import { requireDateParamsContext } from "./date-params.svelte";
import { contextResource } from "./resource-context.svelte";

/**
 * The date range, fetch and row list every actogram-backed report needs.
 *
 * Rows are the days the API serves in the patient's zone, newest-first, and
 * stop at the picker's last day, so the padding after `to` feeds
 * each row's next-day double plot without appearing as a row of its own.
 */
export function useActogramReport(errorTitle: string, defaultDays = 14) {
  const params = requireDateParamsContext(defaultDays);

  const resource = contextResource(
    () => getActogramRange({ from: params.fromDay, to: params.toDay }),
    { errorTitle }
  );

  const days = $derived(
    (resource.current?.days ?? [])
      .filter((day) => day.date <= params.toDay)
      .reverse()
  );

  return {
    params,
    resource,
    get days() {
      return days;
    },
    /** Rows inside the picker's range; the rest of `days` is scroll-back padding. */
    get rangeDayCount() {
      return days.filter((day) => day.date >= params.fromDay).length;
    },
  };
}
