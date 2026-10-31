import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { CustomReportCatalogue, CustomReportRunResponse } from "#lib/api/generated/nocturne-api-client.js";
import { catalogue as fixtureCatalogue, statisticsTemplate } from "#lib/components/reports/custom/builder/schema-form.fixtures.js";
import { statTilesResult } from "../custom-report-fixtures.js";

const calls = vi.hoisted(() => ({ runs: [] as unknown[], creates: [] as unknown[], gotos: [] as string[] }));
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
  getCustomReportCatalogue: () => remoteQuery(() => catalogue),
  getCustomReportTemplates: () => remoteQuery(() => [statisticsTemplate]),
  getCustomReports: () => remoteQuery(() => []),
  runCustomReport: (arg: { spec: Record<string, unknown> }) => {
    calls.runs.push(arg);
    return remoteQuery(() => respond(arg.spec));
  },
  createCustomReport: remoteCommand((arg) => {
    calls.creates.push(arg);
    return { id: "new-id", name: "Saved" };
  }),
}));

vi.mock("$app/navigation", () => ({
  goto: (url: string) => {
    calls.gotos.push(url);
    return Promise.resolve();
  },
}));

import NewCustomReportPage from "./+page.svelte";

const lastRun = () => calls.runs.at(-1) as { spec: { title?: string; panels: { dataset: Record<string, unknown> }[] } };

describe("the report builder on a new report", () => {
  beforeEach(() => {
    calls.runs.length = 0;
    calls.creates.length = 0;
    calls.gotos.length = 0;
    catalogue = structuredClone(fixtureCatalogue);
    respond = (spec) => ({ result: statTilesResult(), spec, errors: [] });
  });

  it("opens a template, previews it, and saves it under a name", async () => {
    render(NewCustomReportPage, {});

    await page.getByRole("button", { name: /Statistics/ }).click();

    await vi.waitFor(() => expect(calls.runs.length).toBeGreaterThan(0));
    expect(lastRun().spec).toMatchObject({ title: "Two weeks", panels: [{ id: "p1", dataset: { name: "glucose_statistics" } }] });
    await expect.element(page.getByText("72.4")).toBeVisible();

    const name = page.getByLabelText("Name", { exact: true });
    await expect.element(name).toHaveValue("Two weeks");
    await name.fill("Fortnight");
    await page.getByRole("button", { name: "Save", exact: true }).click();

    await vi.waitFor(() => expect(calls.creates).toHaveLength(1));
    expect(calls.creates[0]).toMatchObject({ name: "Fortnight", spec: { title: "Two weeks", version: 1 } });
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom/new-id"]));
  });

  it("starts blank and adds panels up to the most a report holds, then says why it cannot add more", async () => {
    catalogue.maxPanels = 2;
    render(NewCustomReportPage, {});
    await page.getByRole("button", { name: /Blank/ }).click();
    await expect.element(page.getByText("This report has no panels. Add a panel to start.")).toBeVisible();

    const add = page.getByTestId("add-panel");
    await add.click();
    await add.click();

    await expect.element(page.getByTestId("panel-card").nth(1)).toBeVisible();
    await expect.element(add).toBeDisabled();
    await expect.element(page.getByTestId("panel-limit")).toHaveTextContent("A report can have at most 2 panels.");
  });

  it("draws a form control for every kind of setting the catalogue gives", async () => {
    render(NewCustomReportPage, {});
    await page.getByRole("button", { name: /Blank/ }).click();
    await page.getByTestId("add-panel").click();

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
    render(NewCustomReportPage, {});
    await page.getByRole("button", { name: /Blank/ }).click();
    await page.getByTestId("add-panel").click();
    await page.getByLabelText("Data").click();
    await page.getByRole("option", { name: /Every setting/ }).click();

    const field = page.getByTestId("panel-card").element().querySelector('[data-parameter="p4"]') as HTMLElement;
    await vi.waitFor(() => expect(field.textContent).toContain("Send a whole number, 1 to 60."), { timeout: 3000 });
    expect(page.getByTestId("panel-card").element().querySelector('[data-parameter="p3"]')?.textContent).not.toContain("Send a whole number");
    await expect.element(page.getByText("Correct the problems in the report to see the preview.")).toBeVisible();
  });

  it("tells a caller with glucose access only what its preview leaves out", async () => {
    respond = (spec) => {
      const result = statTilesResult();
      return { result: { ...result, panels: [{ ...result.panels[0], omitted: [{ field: "tdd", scope: "treatments.read" }] }] }, spec, errors: [] };
    };
    render(NewCustomReportPage, {});
    await page.getByRole("button", { name: /Statistics/ }).click();

    await expect.element(page.getByText("Not shown, because your access does not include it: Insulin per day.")).toBeVisible();

    await page.getByLabelText("Data").click();
    await page.getByRole("option", { name: /Treatments/ }).click();
    await expect.element(page.getByText("Your access does not include this data, so this panel shows nothing.")).toBeVisible();
  });
});
