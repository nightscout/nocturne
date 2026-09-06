import { getActogramData } from "$api/actogram.remote";
import { ACTOGRAM_PADDING_DAYS, buildDayRange } from "$lib/components/actogram";
import { MS_PER_DAY } from "$lib/components/actogram/actogram";
import { requireDateParamsContext } from "./date-params.svelte";
import { contextResource } from "./resource-context.svelte";

/**
 * The date range, fetch and row list every actogram-backed report needs.
 *
 * Rows run newest-first and stop at the picker's range end, so the padding
 * after `to` feeds each row's next-day double plot without appearing as a row
 * of its own.
 */
export function useActogramReport(errorTitle: string, defaultDays = 14) {
  const params = requireDateParamsContext(defaultDays);

  const paddedFrom = $derived(
    params.dateRangeMillis.from - ACTOGRAM_PADDING_DAYS * MS_PER_DAY
  );

  const resource = contextResource(
    () =>
      getActogramData({
        from: paddedFrom,
        to: params.dateRangeMillis.to + ACTOGRAM_PADDING_DAYS * MS_PER_DAY,
      }),
    { errorTitle }
  );

  const days = $derived(
    buildDayRange(paddedFrom, params.dateRangeMillis.to).reverse()
  );

  return {
    params,
    resource,
    get days() {
      return days;
    },
  };
}
