import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { CustomReportCatalogue, CustomReportRunResponse } from "#lib/api/generated/nocturne-api-client.js";
import { catalogue as fixtureCatalogue, chartTemplate } from "#lib/components/reports/custom/builder/schema-form.fixtures.js";

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
  getCustomReportTemplates: () => remoteQuery(() => [chartTemplate]),
  getCustomReports: () => remoteQuery(() => []),
  runCustomReport: remoteCommand((arg) => {
    calls.runs.push(arg);
    return respond((arg as { spec: Record<string, unknown> }).spec);
  }),
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

type Spec = { panels: { id: string; tracks: { id: string; layers: { id: string; derived?: Record<string, unknown> }[] }[] }[] };
const lastSpec = () => (calls.runs.at(-1) as { spec: Spec }).spec;

const from = Date.UTC(2026, 9, 29, 6);
const to = from + 6 * 3_600_000;

/** The API's run of a chart spec: each layer a line over two readings, its side by its unit, the IOB layer on the right. */
function chartRun(spec: Record<string, unknown>): CustomReportRunResponse {
  const panels = (spec as unknown as Spec).panels.map((p) => ({
    id: p.id,
    title: "Glucose",
    kind: "chart",
    xKind: "time",
    status: "ok",
    window: { from, to },
    fields: [],
    rows: [],
    summary: "Glucose.",
    sentence: "0 episodes below range and 0 above it.",
    tracks: p.tracks.map((t) => ({
      id: t.id,
      height: 1,
      layers: t.layers.map((l) => ({
        id: l.id,
        title: l.id === "iob" ? "IOB" : "Glucose",
        mark: "line",
        bindings: { x: "time", y: [l.id === "iob" ? "iob" : "glucose"] },
        xKind: "time",
        unit: l.id === "iob" ? "U" : "mg/dL",
        side: l.id === "iob" ? "right" : "left",
        status: l.derived ? "not_permitted" : "ok",
        sentence: l.derived ? "Not shown: a source of this layer needs the treatments permission." : null,
        window: { from, to },
        fields: [
          { name: "time", label: "Time", kind: "time" },
          { name: "glucose", label: "Glucose", kind: "number", unit: "mg/dL", color: "glucose-in-range", glucoseScale: true },
          { name: "iob", label: "IOB", kind: "number", unit: "U", color: "insulin-bolus" },
        ],
        rows: [{ time: from, glucose: 110, iob: 1 }, { time: to, glucose: 140, iob: 2 }],
      })),
    })),
  }));
  return { result: { spec: { version: 1, title: "Glucose" }, units: "mg/dl", summary: "", warnings: [], timeZone: "UTC", panels }, spec, errors: [] };
}

describe("the report builder on a chart of tracks of layers", () => {
  beforeEach(() => {
    calls.runs.length = 0;
    calls.creates.length = 0;
    calls.gotos.length = 0;
    catalogue = structuredClone(fixtureCatalogue);
    respond = chartRun;
    pageState.data = {};
  });

  async function openTemplate() {
    render(NewCustomReportPage, {});
    await page.getByRole("button", { name: /Glucose chart/ }).click();
    await vi.waitFor(() => expect(calls.runs.length).toBeGreaterThan(0));
  }

  it("opens a chart template as tracks of layer rows, previews it with each layer's axis, and saves it", async () => {
    await openTemplate();

    await expect.element(page.getByTestId("track-section").nth(1)).toBeVisible();
    const rows = page.getByTestId("layer-row");
    await expect.element(rows.nth(0).getByTestId("layer-side")).toHaveTextContent("Left axis, mg/dL");
    await expect.element(rows.nth(1).getByTestId("layer-side")).toHaveTextContent("Right axis, U");
    await expect.element(page.getByTestId("report-layered-chart")).toBeVisible();

    await page.getByLabelText("Name", { exact: true }).fill("Glucose and IOB");
    await page.getByRole("button", { name: "Save", exact: true }).click();
    await vi.waitFor(() => expect(calls.creates).toHaveLength(1));
    expect(calls.creates[0]).toMatchObject({ name: "Glucose and IOB", spec: { panels: [{ kind: "chart", tracks: [{ id: "glucose" }, { id: "onboard" }] }] } });
  });

  it("adds a track with a layer of its own", async () => {
    await openTemplate();

    await page.getByTestId("add-track").click();

    await vi.waitFor(() => expect(lastSpec().panels[0].tracks.map((t) => t.id)).toEqual(["glucose", "onboard", "t3"]), { timeout: 3000 });
    await expect.element(page.getByTestId("track-section").nth(2).getByTestId("layer-row")).toBeVisible();
  });

  it("adds a derived layer, copies a source from another layer, and shows that the layer was not drawn", async () => {
    await openTemplate();
    const track = page.getByTestId("track-section").nth(0);
    await track.getByTestId("add-layer").click();

    const row = track.getByTestId("layer-row").nth(1);
    await row.getByLabelText("Data").first().click();
    await page.getByRole("option", { name: /Scatter/ }).click();

    await expect.element(row.getByText("First source")).toBeVisible();
    await expect.element(row.getByText("Second source")).toBeVisible();
    await vi.waitFor(() => expect(lastSpec().panels[0].tracks[0].layers[1].derived).toMatchObject({
      op: "scatter", a: { dataset: { name: "glucose_series" }, field: "glucose" }, b: { field: "glucose" },
    }), { timeout: 3000 });

    await row.getByTestId("copy-source").first().click();
    await page.getByRole("option", { name: "glucose_series" }).or(page.getByRole("option", { name: "Glucose readings" })).last().click();
    await vi.waitFor(() => expect(lastSpec().panels[0].tracks[0].layers[1].derived).toMatchObject({ a: { field: "iob" } }), { timeout: 3000 });

    await expect.element(row.getByTestId("layer-status")).toHaveTextContent("needs the treatments permission");
  });

  it("puts the validator's unit message on the layer row its path names", async () => {
    respond = (spec) => ({
      result: undefined,
      spec,
      errors: [{ path: "panels[0].tracks[1].layers[1]", message: "This track has a left axis in U and a right axis in mg/dL; this layer is in g." }],
    });
    await openTemplate();
    const track = page.getByTestId("track-section").nth(1);
    await track.getByTestId("add-layer").click();

    const row = track.getByTestId("layer-row").nth(1);
    await expect.element(row.getByTestId("field-error")).toHaveTextContent("this layer is in g.");
    expect(track.getByTestId("layer-row").nth(0).element().textContent).not.toContain("this layer is in g.");
  });

  it("adds layers up to the most a chart holds, then says why it cannot add more", async () => {
    catalogue.maxLayers = 3;
    await openTemplate();

    const add = page.getByTestId("add-layer").nth(0);
    await add.click();

    await expect.element(add).toBeDisabled();
    await expect.element(page.getByTestId("add-track")).toBeDisabled();
    await expect.element(page.getByTestId("layer-limit").first()).toHaveTextContent("A chart can have at most 3 layers.");
  });
});
