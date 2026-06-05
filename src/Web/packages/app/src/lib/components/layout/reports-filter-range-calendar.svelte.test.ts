import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import { getLocalTimeZone, today } from "@internationalized/date";
import { RangeCalendar } from "$lib/components/ui/range-calendar";

// The reports filter and the shared date-range-picker render a RangeCalendar
// with a selected range. When the app and bits-ui resolve different copies of
// @internationalized/date, the date values fail bits-ui's `instanceof
// CalendarDate` checks and it throws "Unknown date type" on the day cells,
// blanking the filter. Deduping @internationalized/date to one version fixes it.
describe("reports filter RangeCalendar", () => {
  it("mounts with a selected range without throwing 'Unknown date type'", async () => {
    const end = today(getLocalTimeZone());
    const start = end.subtract({ days: 6 });
    render(RangeCalendar, { value: { start, end } });
    await expect.element(page.getByRole("grid").first()).toBeVisible();
  });
});
