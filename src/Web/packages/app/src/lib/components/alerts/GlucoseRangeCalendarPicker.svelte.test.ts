import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

vi.mock("#lib/api/generated/statistics.generated.remote.js", () => ({
  getPunchCardData: () => remoteQuery(() => ({ months: [] })),
}));

import GlucoseRangeCalendarPicker from "./GlucoseRangeCalendarPicker.svelte";

describe("GlucoseRangeCalendarPicker", () => {
  it("fits a 360px viewport", async () => {
    const original = [window.innerWidth, window.innerHeight] as const;
    await page.viewport(360, 800);
    try {
      const screen = render(GlucoseRangeCalendarPicker);
      await expect.element(page.getByLabelText("Next month")).toBeVisible();

      const box = screen.container.firstElementChild!.getBoundingClientRect();
      expect(box.left).toBeGreaterThanOrEqual(0);
      expect(box.right).toBeLessThanOrEqual(360);
      expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(360);
    } finally {
      await page.viewport(original[0], original[1]);
    }
  });

  it("disables the days that would stretch a range past the cap", async () => {
    render(GlucoseRangeCalendarPicker, { startDate: "2000-01-10", endDate: "2000-01-11", maxDate: "2000-01-31", maxDays: 3 });

    await page.getByRole("button", { name: /January 10, 2000/ }).click();

    await expect.element(page.getByRole("button", { name: /January 12, 2000/ })).toBeEnabled();
    await expect.element(page.getByRole("button", { name: /January 13, 2000/ })).toBeDisabled();
    await expect.element(page.getByRole("button", { name: /January 7, 2000/ })).toBeDisabled();
  });

  it("allows any range when there is no cap", async () => {
    render(GlucoseRangeCalendarPicker, { startDate: "2000-01-10", endDate: "2000-01-11", maxDate: "2000-01-31" });

    await page.getByRole("button", { name: /January 10, 2000/ }).click();

    await expect.element(page.getByRole("button", { name: /January 30, 2000/ })).toBeEnabled();
  });
});
