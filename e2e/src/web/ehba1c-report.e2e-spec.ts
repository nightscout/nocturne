import { postEntries, sgvSeries } from "../helpers/data.ts";
import { expect, signIn, test } from "./fixtures.ts";

test.describe("eHbA1c report", () => {
  test("plots a lab result and names it in the tooltip", async ({ page, seed }) => {
    const tenant = await seed();
    // Two days of readings: each timeline point needs readings in its trailing window, and
    // uploading them directly is far quicker than the sample-data seeder.
    await postEntries(tenant.api, sgvSeries({ count: 2 * 288, valueAt: (i) => 140 + 30 * Math.sin(i / 20) }));
    const measuredAt = new Date(Date.now() - 5 * 24 * 60 * 60 * 1000);
    measuredAt.setUTCHours(0, 0, 0, 0);
    await tenant.api.ok("POST", "/api/v4/lab-results/hba1c", {
      measuredAt: measuredAt.toISOString(),
      valuePercent: 6.4,
      note: "E2E lab draw",
    });

    await signIn(page, tenant);
    await page.goto(`${tenant.webUrl}/reports/ehba1c`);

    const chart = page.getByTestId("ehba1c-chart");
    await expect(chart).toBeVisible();
    const marker = page.getByTestId("lab-marker").first();
    await expect(marker).toBeVisible();

    const box = await marker.boundingBox();
    expect(box).not.toBeNull();
    await page.mouse.move(box!.x + box!.width / 2, box!.y + box!.height / 2);

    const tooltip = page.getByTestId("ehba1c-tooltip");
    await expect(tooltip).toBeVisible();
    await expect(tooltip).toContainText("Lab result");
    await expect(tooltip).toContainText("6.4");
    await expect(tooltip).toContainText("E2E lab draw");

    await expect(page.getByText("Lab results", { exact: true })).toBeVisible();
  });
});
