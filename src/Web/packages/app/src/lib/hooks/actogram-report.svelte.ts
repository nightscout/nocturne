import { getActogramData } from "$api/actogram.remote";
import { ACTOGRAM_PADDING_DAYS, buildDayRange } from "$lib/components/actogram";
import { MS_PER_DAY } from "$lib/components/actogram/actogram";
import { requireDateParamsContext } from "./date-params.svelte";
import { contextResource } from "./resource-context.svelte";

/**
 * The date range, fetch and row list every actogram-backed report needs.
 *
 * The fetch window is padded either side of the picker range so each row's
 * double plot has a next day to draw. Rows run newest-first and stop at the
 * range end — never the future — so the padding after `to` feeds the plots
 * without appearing as rows of its own, while the padding before `from` is
 * there to scroll back into.
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
