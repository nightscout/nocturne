import { randomUUID } from "node:crypto";
import { minutesAgo, postEntries, postTreatments, sgvSeries } from "../helpers/data.ts";
import type { Page } from "@playwright/test";
import { expect, signIn, test } from "./fixtures.ts";

interface CustomReport {
  id: string;
  name: string;
  spec: { title?: string; panels: unknown[]; builder?: { kind: string; version: number; state: unknown } };
}

interface Template {
  id: string;
  spec: { title?: string; panels: unknown[] };
}

const ID = /\/reports\/custom\/([0-9a-f-]{36})/;

/** The workspace's blocks, in order. */
const blocksOf = (page: Page) => page.getByTestId("report-blocks").locator(":scope > li");

test.describe("custom report builder", () => {
  test("makes a report from a question, saves it with the workspace's state, and reopens it there", async ({ page, seed }) => {
    const tenant = await seed();
    await postEntries(tenant.api, sgvSeries({ count: 96, end: Date.now(), valueAt: (i) => Math.round(140 + 60 * Math.sin(i / 8)) }));

    await signIn(page, tenant);
    await page.goto(`${tenant.webUrl}/reports/custom/new`);
    await page.getByText("The last 24 hours", { exact: true }).click();
    await expect(page.getByTestId("chart-preview").getByTestId("report-layered-chart")).toBeVisible();

    await page.getByTestId("save-trigger").click();
    await page.getByLabel("Name", { exact: true }).fill("Workspace e2e");
    await page.getByRole("button", { name: "Save the report" }).click();
    await expect(page).toHaveURL(ID);
    const id = ID.exec(page.url())![1];

    const saved = await tenant.api.ok<CustomReport>("GET", `/api/v4/reports/custom/${id}`);
    expect(saved.spec.builder).toMatchObject({ kind: "workspace", version: 1 });

    await page.goto(`${tenant.webUrl}/reports/custom/${id}/edit`);
    await expect(page.getByTestId("report-workspace")).toBeVisible();
    await expect(page.getByTestId("opened-in-form")).toHaveCount(0);
    await expect(page.getByLabel("Report title")).toHaveValue("The last 24 hours");
    await expect(page.getByRole("radio", { name: "Timeline" })).toHaveAttribute("data-state", "on");
    await expect(page.getByTestId("chart-caption")).toContainText("as a timeline over the last 24 hours");
    const chips = page.getByTestId("signal-chips");
    for (const name of ["Glucose", "Target range", "Insulin on board", "Carbs on board"])
      await expect(chips.getByRole("button", { name: new RegExp(`^${name}`) }).first()).toBeVisible();
    await expect(page.getByTestId("chart-preview").getByTestId("report-layered-chart")).toBeVisible();
  });

  test("opens a report made elsewhere in the workspace, and saves an edit to it", async ({ page, seed }) => {
    const tenant = await seed();
    // Every reading in range, so the report's time in range is 100%.
    await postEntries(tenant.api, sgvSeries({ count: 48, end: Date.now(), valueAt: (i) => 100 + (i % 12) * 5 }));
    const templates = await tenant.api.ok<Template[]>("GET", "/api/v4/reports/custom/templates");
    const agp = templates.find((t) => t.id === "agp")!;
    const { id } = await tenant.api.ok<CustomReport>("POST", "/api/v4/reports/custom", { name: "Builder e2e", spec: agp.spec });

    await signIn(page, tenant);
    await page.goto(`${tenant.webUrl}/reports/custom/${id}`);
    await expect(page.getByRole("heading", { name: "Builder e2e" })).toBeVisible();
    await page.getByRole("link", { name: "Edit" }).click();
    await expect(page).toHaveURL(new RegExp(`/reports/custom/${id}/edit`));
    await expect(page.getByTestId("report-workspace")).toBeVisible();
    await expect(blocksOf(page)).toHaveCount(agp.spec.panels.length);
    await expect(page.getByTestId("spec-errors")).toHaveCount(0);

    const figures = blocksOf(page).first().getByRole("region", { name: "Key figures" });
    await expect(figures.getByText("Time in range", { exact: true }).locator("..")).toContainText("100");
    await page.getByLabel("Report title").fill("Two weeks of glucose");
    await page.getByTestId("save-trigger").click();
    await expect(page.getByLabel("Name", { exact: true })).toHaveValue("Builder e2e");
    await page.getByRole("button", { name: "Save the report" }).click();
    await expect(page).toHaveURL(new RegExp(`/reports/custom/${id}(\\?.*)?$`));
    await expect(page.getByRole("heading", { name: "Two weeks of glucose" })).toBeVisible();

    const edited = await tenant.api.ok<CustomReport>("GET", `/api/v4/reports/custom/${id}`);
    expect(edited.id).toBe(id);
    expect(edited.name).toBe("Builder e2e");
    expect(edited.spec.title).toBe("Two weeks of glucose");
    expect(edited.spec.panels).toEqual(agp.spec.panels);

    // A save clears the changes this browser kept, so the report opens again as saved.
    await page.goto(`${tenant.webUrl}/reports/custom/${id}/edit`);
    await expect(page.getByLabel("Report title")).toHaveValue("Two weeks of glucose");
    await expect(page.getByText("Restored your unsaved changes.")).toHaveCount(0);
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
        kind: "table",
        dataset: { name: "event_response", cohorts: [cohort(), cohort(), cohort()] },
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
    await expect(page.getByTestId("report-workspace")).toBeVisible();
    await expect(blocksOf(page)).toHaveCount(4);
    await expect(page.getByTestId("opening-failed")).toHaveCount(0);
    await expect(page.getByTestId("spec-errors")).toHaveCount(0);
    const preview = blocksOf(page).first().getByTestId("chart-preview");
    await expect(preview.getByRole("heading", { name: "Meals 1" })).toBeVisible();
    await expect(preview.getByRole("alert")).toHaveCount(0);
    for (let i = 2; i <= 4; i++)
      await expect(blocksOf(page).nth(i - 1).getByRole("heading", { name: `Meals ${i}` }).first()).toBeVisible();
  });

  test("opens a saved Insulin and glucose report, and downloads a chart of it as a PNG", async ({ page, seed }) => {
    const tenant = await seed();
    // Eight hours of glucose that rises and falls, and a bolus every two hours, so IOB varies with it.
    await postEntries(tenant.api, sgvSeries({ count: 96, end: Date.now(), valueAt: (i) => Math.round(140 + 60 * Math.sin(i / 8)) }));
    await postTreatments(tenant.api, Array.from({ length: 4 }, (_, i) => ({
      eventType: "Correction Bolus",
      insulin: 1 + i,
      created_at: minutesAgo(30 + i * 120),
    })));

    const templates = await tenant.api.ok<Template[]>("GET", "/api/v4/reports/custom/templates");
    const template = templates.find((t) => t.id === "insulin-glucose")!;
    const { id } = await tenant.api.ok<CustomReport>("POST", "/api/v4/reports/custom", { name: "Insulin and glucose e2e", spec: template.spec });

    await signIn(page, tenant);
    await page.goto(`${tenant.webUrl}/reports/custom/${id}/edit`);
    await expect(blocksOf(page)).toHaveCount(2);
    const open = blocksOf(page).first();
    await expect(open).toHaveAttribute("data-open", "true");
    const rail = open.getByRole("group", { name: "On this chart" });
    await expect(rail.getByRole("listitem").first()).toBeVisible();
    await expect(rail.getByRole("button", { name: /^Remove .+ from the chart$/ }).first()).toBeVisible();
    await expect(open.getByTestId("chart-preview").getByTestId("report-layered-chart").first()).toBeVisible();

    await page.goto(`${tenant.webUrl}/reports/custom/${id}`);
    await expect(page.getByRole("heading", { name: "Glucose against insulin on board" })).toBeVisible();
    await expect(page.getByTestId("report-x-caption").first()).toHaveText("IOB (U)");
    await expect(page.getByText("Correlation of IOB with glucose shifted earlier and later")).toBeVisible();

    const download = page.waitForEvent("download");
    await page.getByTestId("download-panel-png").first().click();
    const file = await download;
    expect(file.suggestedFilename()).toBe("glucose-against-insulin-on-board.png");
    const path = await file.path();
    const { readFile } = await import("node:fs/promises");
    const png = await readFile(path);
    expect([...png.subarray(0, 4)]).toEqual([0x89, 0x50, 0x4e, 0x47]);
    expect(png.length).toBeGreaterThan(10_000);
  });
});
