import { randomUUID } from "node:crypto";
import { postEntries, sgvSeries } from "../helpers/data.ts";
import { expect, signIn, test } from "./fixtures.ts";

interface CustomReport {
  id: string;
  name: string;
  spec: { title?: string; panels: unknown[] };
}

interface Template {
  id: string;
  spec: { title?: string; panels: unknown[] };
}

const ID = /\/reports\/custom\/([0-9a-f-]{36})/;

test.describe("custom report builder", () => {
  test("makes a report from a template, saves it, and saves an edit to it", async ({ page, seed }) => {
    const tenant = await seed();
    // Every reading in range, so the report's time in range is 100%.
    await postEntries(tenant.api, sgvSeries({ count: 48, end: Date.now(), valueAt: (i) => 100 + (i % 12) * 5 }));
    const templates = await tenant.api.ok<Template[]>("GET", "/api/v4/reports/custom/templates");
    const agp = templates.find((t) => t.id === "agp")!;

    await signIn(page, tenant);
    await page.goto(`${tenant.webUrl}/reports/custom/new`);
    await page.getByRole("button", { name: /Ambulatory glucose profile/ }).click();

    const figures = page.getByTestId("panel-card").first().getByTestId("panel-preview");
    await expect(figures.getByText("Time in range", { exact: true }).locator("..")).toContainText("100");

    await page.getByLabel("Name", { exact: true }).fill("Builder e2e");
    await page.getByRole("button", { name: "Save", exact: true }).click();
    await expect(page).toHaveURL(ID);
    const id = ID.exec(page.url())![1];
    await expect(page.getByRole("heading", { name: "Builder e2e" })).toBeVisible();

    const saved = await tenant.api.ok<CustomReport>("GET", `/api/v4/reports/custom/${id}`);
    expect(saved.spec).toEqual(agp.spec);

    await page.getByRole("link", { name: "Edit" }).click();
    await expect(page).toHaveURL(new RegExp(`/reports/custom/${id}/edit`));
    await page.getByLabel("Title", { exact: true }).fill("Two weeks of glucose");
    await page.getByRole("button", { name: "Save", exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/reports/custom/${id}(\\?.*)?$`));
    await expect(page.getByRole("heading", { name: "Two weeks of glucose" })).toBeVisible();

    const edited = await tenant.api.ok<CustomReport>("GET", `/api/v4/reports/custom/${id}`);
    expect(edited.id).toBe(id);
    expect(edited.spec.title).toBe("Two weeks of glucose");
    expect(edited.spec.panels).toEqual(agp.spec.panels);
  });

  test("opens a saved report whose spec is longer than a request line can carry", async ({ page, seed }) => {
    const tenant = await seed();
    const cohort = () => ({ anchor: "Meal", foods: Array.from({ length: 20 }, () => randomUUID()) });
    const spec = {
      version: 1,
      title: "Many foods",
      panels: Array.from({ length: 4 }, (_, i) => ({
        id: `meals${i}`,
        title: `Meals ${i + 1}`,
        dataset: { name: "event_response", cohorts: [cohort(), cohort(), cohort()] },
        primitive: "table",
        range: { hours: 72 },
      })),
    };
    expect(JSON.stringify(spec).length).toBeGreaterThan(8 * 1024);
    const saved = await tenant.api.ok<CustomReport>("POST", "/api/v4/reports/custom", { name: "Many foods", spec });

    await signIn(page, tenant);
    await page.goto(`${tenant.webUrl}/reports/custom/${saved.id}`);

    for (let i = 1; i <= 4; i++) await expect(page.getByRole("heading", { name: `Meals ${i}` })).toBeVisible();
    await expect(page.getByText("Could not run the report")).toHaveCount(0);

    await page.goto(`${tenant.webUrl}/reports/custom/${saved.id}/edit`);
    await expect(page.getByTestId("panel-card")).toHaveCount(4);
    await expect(page.getByTestId("panel-preview").first().getByText("Correct the problems")).toHaveCount(0);
    await expect(page.getByTestId("panel-preview").first().getByRole("heading", { name: "Meals 1" })).toBeVisible();
  });
});
