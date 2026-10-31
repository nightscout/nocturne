import { postEntries, sgvSeries } from "../helpers/data.ts";
import { expect, signIn, test } from "./fixtures.ts";

test.describe("custom report builder", () => {
  test("makes a report from a template, saves it, and saves an edit to it", async ({ page, seed }) => {
    const tenant = await seed();
    await postEntries(tenant.api, sgvSeries({ count: 48, end: Date.now(), valueAt: (i) => 100 + (i % 12) * 5 }));

    await signIn(page, tenant);
    await page.goto(`${tenant.webUrl}/reports/custom/new`);
    await page.getByRole("button", { name: /Ambulatory glucose profile/ }).click();

    const first = page.getByTestId("panel-card").first();
    await expect(first.getByTestId("panel-preview")).toContainText("Time in range");

    await page.getByLabel("Name", { exact: true }).fill("Builder e2e");
    await page.getByRole("button", { name: "Save", exact: true }).click();
    await expect(page).toHaveURL(/\/reports\/custom\/[0-9a-f-]{36}/);
    await expect(page.getByRole("heading", { name: "Builder e2e" })).toBeVisible();

    await page.getByRole("link", { name: "Edit" }).click();
    await expect(page).toHaveURL(/\/edit/);
    await page.getByLabel("Title", { exact: true }).fill("Two weeks of glucose");
    await page.getByRole("button", { name: "Save", exact: true }).click();
    await expect(page).toHaveURL(/\/reports\/custom\/[0-9a-f-]{36}(\?.*)?$/);
    await expect(page.getByRole("heading", { name: "Two weeks of glucose" })).toBeVisible();
  });
});
