import { postEntries, sgvSeries } from "../helpers/data.ts";
import { expect, signIn, test } from "./fixtures.ts";

test.describe("dashboard", () => {
  test("shows the latest uploaded glucose", async ({ page, seed }) => {
    const tenant = await seed();
    // A distinctive newest reading, so the assertion cannot match anything else on the page.
    await postEntries(tenant.api, sgvSeries({ count: 24, valueAt: (i) => (i === 0 ? 187 : 110 + i) }));

    await signIn(page, tenant);

    await expect(page.getByTestId("first-reading-empty-state")).toHaveCount(0);
    const recent = page.locator('[data-slot="card"]').filter({ hasText: "Recent readings" });
    await expect(recent).toContainText("187");
    await expect(page.locator("main")).toContainText("187");
  });

  test("a tenant without readings sees the first-reading empty state", async ({ page, seed }) => {
    const tenant = await seed();
    await signIn(page, tenant);
    await expect(page.getByTestId("first-reading-empty-state")).toBeVisible();
  });

  test("renders a seeded history across the dashboard widgets", async ({ page, seed }) => {
    const tenant = await seed({ sampleData: true, sampleDataDays: 2 });
    await signIn(page, tenant);
    await expect(page.locator("main")).toContainText("Time in Range");
    await expect(page.locator("main")).toContainText(/\d+ readings/);
  });
});
