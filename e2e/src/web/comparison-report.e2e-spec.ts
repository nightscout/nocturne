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

    const duration = page.getByText("Hypo Duration", { exact: true }).locator("..");
    await expect(duration).toContainText("0.0 h");
    await expect(duration).toContainText("0.5 h");
    await expect(duration).toContainText("+0.5 h");
    const events = page.getByText("Hypo Events", { exact: true }).locator("..");
    await expect(events.locator(":scope > div").nth(1)).toHaveText("0");
    await expect(events.locator(":scope > div").nth(2)).toHaveText("1");
    await expect(events).toContainText("+1");
    await expect(page.getByText("Hyper Duration", { exact: true })).toBeVisible();
    await expect(page.getByText("Hyper Events", { exact: true })).toBeVisible();
  });

  test("distinguishes an empty period from a measured period without lows", async ({ page, seed }) => {
    const tenant = await seed();
    await postEntries(tenant.api, sgvSeries({ count: 8, end: Date.parse("2026-09-02T12:00:00Z"), valueAt: () => 100 }));

    await signIn(page, tenant);
    await page.goto(`${tenant.webUrl}${comparisonPath}`);

    for (const label of ["Hypo Duration", "Hypo Events"]) {
      const cells = page.getByText(label, { exact: true }).locator("..").locator(":scope > div");
      await expect(cells.nth(1)).toHaveText("No data");
      await expect(cells.nth(2)).toHaveText(label === "Hypo Duration" ? "0.0 h" : "0");
      await expect(cells.nth(4)).toHaveText("—");
    }
  });
});
