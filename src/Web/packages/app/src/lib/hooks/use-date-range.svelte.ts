import { page } from "$app/state";
import { getDateRangeInputFromUrl } from "$lib/utils/date-range";

export function useDateRange(defaultDays = 7) {
  return getDateRangeInputFromUrl(page.url, defaultDays);
}

