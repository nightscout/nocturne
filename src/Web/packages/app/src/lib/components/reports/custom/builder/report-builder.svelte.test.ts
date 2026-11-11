import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { CustomReportCatalogue, CustomReportRunResponse } from "#lib/api/generated/nocturne-api-client.js";
import { statTilesResult } from "../../../../../routes/(authenticated)/reports/custom/custom-report-fixtures.js";
import { catalogue as fixtureCatalogue, resultFor, statisticsTemplate } from "./schema-form.fixtures.js";

const calls = vi.hoisted(() => ({ runs: [] as unknown[], saves: [] as Record<string, unknown>[] }));
let catalogue: CustomReportCatalogue;
let respond: (spec: Record<string, unknown>) => CustomReportRunResponse;

vi.mock("#lib/api/report-image.remote.js", () => ({
  drawReportPanelImage: () => Promise.resolve(""),
}));

vi.mock("#lib/api/generated/foods.generated.remote.js", () => ({
  getFoods: () => remoteQuery(() => []),
  getFood: () => remoteQuery(() => ({ id: "f", name: "Penne" })),
}));

vi.mock("#lib/api/generated/customReports.generated.remote.js", () => ({
  runCustomReport: remoteCommand((arg) => {
    calls.runs.push(arg);
    return respond((arg as { spec: Record<string, unknown> }).spec);
  }),
}));

import ReportBuilderHarness from "./ReportBuilder.test-harness.svelte";

const blank = { version: 1, panels: [] };
const open = (spec: unknown) => render(ReportBuilderHarness, { catalogue, spec, onsave: (s) => calls.saves.push(s) });

const lastRun = () => calls.runs.at(-1) as { spec: { title?: string; panels: { dataset: Record<string, unknown> }[] } };

describe("the form builder", () => {
  beforeEach(() => {
    calls.runs.length = 0;
    calls.saves.length = 0;
    catalogue = structuredClone(fixtureCatalogue);
    respond = (spec) => ({ result: resultFor(spec, statTilesResult()), spec, errors: [] });
  });

  it("opens a template, previews it, and hands the page its spec", async () => {
    open(statisticsTemplate.spec);

    await vi.waitFor(() => expect(calls.runs.length).toBeGreaterThan(0));
    expect(lastRun().spec).toMatchObject({ title: "Two weeks", panels: [{ id: "p1", dataset: { name: "glucose_statistics" } }] });
    await expect.element(page.getByText("72.4")).toBeVisible();

    await page.getByRole("button", { name: "Save", exact: true }).click();

    await vi.waitFor(() => expect(calls.saves).toHaveLength(1));
    expect(calls.saves[0]).toMatchObject({ title: "Two weeks", version: 1 });
  });

  it("starts blank and adds panels up to the most a report holds, then says why it cannot add more", async () => {
    catalogue.maxPanels = 2;
    open(blank);
    await expect.element(page.getByText("This report has no panels. Add a panel to start.")).toBeVisible();

    const add = page.getByTestId("add-panel");
    await add.click();
    await add.click();

    await expect.element(page.getByTestId("panel-card").nth(1)).toBeVisible();
    await expect.element(add).toBeDisabled();
    await expect.element(page.getByTestId("panel-limit")).toHaveTextContent("A report can have at most 2 panels.");
  });

  it("draws a form control for every kind of setting the catalogue gives", async () => {
    open(blank);
    await page.getByTestId("add-panel").click();

    await page.getByLabelText("Panel type").click();
    await page.getByRole("option", { name: "Statistics tiles" }).click();
    await page.getByLabelText("Data").click();
    await page.getByRole("option", { name: /Every setting/ }).click();

    await vi.waitFor(() => {
      const drawn = new Set(
        [...page.getByTestId("panel-card").element().querySelectorAll("[data-control]")].map((e) => e.getAttribute("data-control")),
      );
      for (const control of ["list", "toggles", "foods", "switch", "number", "select", "time", "group", "text", "date", "food-category"])
        expect(drawn, control).toContain(control);
      expect(drawn).not.toContain("none");
    });

    await page.getByRole("switch", { name: "Exclude illness" }).click();
    await vi.waitFor(() => expect(lastRun().spec.panels[0].dataset).toMatchObject({ name: "every_shape", p3: false }), { timeout: 3000 });
  });

  it("puts the validator's message under the setting it names", async () => {
    respond = (spec) => ({
      result: undefined,
      spec,
      errors: [{ path: "panels[0].dataset.p4", message: "Send a whole number, 1 to 60." }],
    });
    open(blank);
    await page.getByTestId("add-panel").click();
    await page.getByLabelText("Panel type").click();
    await page.getByRole("option", { name: "Statistics tiles" }).click();
    await page.getByLabelText("Data").click();
    await page.getByRole("option", { name: /Every setting/ }).click();

    const field = page.getByTestId("panel-card").element().querySelector('[data-parameter="p4"]') as HTMLElement;
    await vi.waitFor(() => expect(field.textContent).toContain("Send a whole number, 1 to 60."), { timeout: 3000 });
    expect(page.getByTestId("panel-card").element().querySelector('[data-parameter="p3"]')?.textContent).not.toContain("Send a whole number");
    await expect.element(page.getByText("Correct the problems in the report to see the preview.")).toBeVisible();
  });

  it("tells a caller with glucose access only what its preview leaves out", async () => {
    respond = (spec) => {
      const result = resultFor(spec, statTilesResult());
      return { result: { ...result, panels: result.panels.map((p) => ({ ...p, omitted: [{ field: "tdd", scope: "treatments.read" }] })) }, spec, errors: [] };
    };
    open(statisticsTemplate.spec);

    await expect.element(page.getByText("Not shown, because your access does not include it: Insulin per day.")).toBeVisible();

    await page.getByLabelText("Data").click();
    await page.getByRole("option", { name: /Treatments/ }).click();
    await expect.element(page.getByText("Your access does not include this data, so this panel shows nothing.")).toBeVisible();
  });

  it("draws each panel's own result after a reorder, and no result of an older spec meanwhile", async () => {
    respond = (spec) => {
      const result = resultFor(spec, statTilesResult());
      return {
        result: { ...result, panels: result.panels.map((p) => ({ ...p, rows: [{ timeInRangePercent: p.id === "p1" ? 11.1 : 22.2 }] })) },
        spec,
        errors: [],
      };
    };
    open(blank);
    await page.getByTestId("add-panel").click();
    await page.getByTestId("add-panel").click();
    const cards = page.getByTestId("panel-card");
    await expect.element(cards.nth(0).getByText("11.1")).toBeVisible();
    await expect.element(cards.nth(1).getByText("22.2")).toBeVisible();

    await page.getByRole("button", { name: "Move panel 2 up" }).click();

    expect(cards.nth(0).element().textContent).not.toContain("11.1");
    await expect.element(cards.nth(0).getByText("22.2")).toBeVisible();
    await expect.element(cards.nth(1).getByText("11.1")).toBeVisible();
  });
});
