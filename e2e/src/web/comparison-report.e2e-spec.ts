import { postEntries, sgvSeries } from "../helpers/data.ts";
import { expect, signIn, test } from "./fixtures.ts";

const comparisonPath = "/reports/comparison?preset=custom&aFrom=2026-09-01&aTo=2026-09-01&bFrom=2026-09-02&bTo=2026-09-02";

test.describe("comparison report", () => {
  test("compares covered low time and counts one excursion across both low zones", async ({ page, seed }) => {
    const tenant = await seed();
    await postEntries(tenant.api, [
      ...sgvSeries({ count: 8, end: Date.parse("2026-09-01T12:00:00Z"), valueAt: () => 100 }),
      ...sgvSeries({ count: 8, end: Date.parse("2026-09-02T12:00:00Z"), valueAt: (i) => [100, 60, 40, 60, 60, 40, 60, 100][i] }),
    ]);

    await signIn(page, tenant);
    await page.goto(`${tenant.webUrl}${comparisonPath}`);

    // Each period covers 40 minutes of readings, so per-day figures scale by 1440 / 40 = 36.
    const time = page.getByText("Hypo time / day", { exact: true }).locator("..");
    await expect(time.locator(":scope > div").nth(1)).toHaveText("0m");
    await expect(time.locator(":scope > div").nth(2)).toHaveText("18h");
    await expect(time).toContainText("+18h");
    const events = page.getByText("Hypo events / day", { exact: true }).locator("..");
    await expect(events.locator(":scope > div").nth(1)).toHaveText("0.00");
    await expect(events.locator(":scope > div").nth(2)).toHaveText("36.00");
    await expect(events).toContainText("+36.00");
    await expect(page.getByText("Hyper time / day", { exact: true })).toBeVisible();
    await expect(page.getByText("Hyper events / day", { exact: true })).toBeVisible();
  });

  test("distinguishes an empty period from a measured period without lows", async ({ page, seed }) => {
    const tenant = await seed();
    await postEntries(tenant.api, sgvSeries({ count: 8, end: Date.parse("2026-09-02T12:00:00Z"), valueAt: () => 100 }));

    await signIn(page, tenant);
    await page.goto(`${tenant.webUrl}${comparisonPath}`);

    for (const label of ["Hypo time / day", "Hypo events / day"]) {
      const cells = page.getByText(label, { exact: true }).locator("..").locator(":scope > div");
      await expect(cells.nth(1)).toHaveText("No data");
      await expect(cells.nth(2)).toHaveText(label === "Hypo time / day" ? "0m" : "0.00");
      await expect(cells.nth(4)).toHaveText("—");
    }
  });
});
