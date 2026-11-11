import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import { liveCatalogue } from "#lib/components/reports/custom/workspace/catalogue.fixtures.js";
import { BUILDER_KIND, BUILDER_VERSION } from "#lib/components/reports/custom/workspace/saved-state.js";
import { runOf } from "../custom-report-fixtures.js";

type Spec = { panels: { id: string; tracks?: { layers: { id: string }[] }[] }[]; builder?: { kind: string; version: number; state: unknown } };

const calls = vi.hoisted(() => ({ runs: [] as { spec: Spec }[], creates: [] as { name: string; spec: Spec }[], gotos: [] as string[] }));
/** The engine's refusal of the next save, or null to save it. */
let refusal: unknown = null;

vi.mock("#lib/api/report-image.remote.js", () => ({ drawReportPanelImage: () => Promise.resolve("") }));

vi.mock("#lib/api/generated/foods.generated.remote.js", () => ({
  getFoods: () => remoteQuery(() => []),
  getFood: () => remoteQuery(() => ({ id: "f", name: "Penne" })),
}));

vi.mock("#lib/api/generated/customReports.generated.remote.js", () => ({
  getCustomReportCatalogue: () => remoteQuery(() => liveCatalogue),
  getCustomReports: () => remoteQuery(() => []),
  runCustomReport: remoteCommand((arg) => {
    const run = arg as { spec: Spec };
    calls.runs.push(run);
    return runOf(run.spec as unknown as Record<string, unknown>);
  }),
  createCustomReport: remoteCommand((arg) => {
    const request = arg as { name: string; spec: Spec };
    if (refusal) return Promise.reject(refusal);
    calls.creates.push(request);
    return { id: "new-id", name: request.name };
  }),
}));

vi.mock("$app/navigation", () => ({
  goto: (url: string) => {
    calls.gotos.push(url);
    return Promise.resolve();
  },
}));

import NewCustomReportPage from "./+page.svelte";

const layerIds = (spec: Spec) => spec.panels.flatMap((p) => (p.tracks ?? []).flatMap((t) => t.layers.map((l) => l.id)));

async function start(question: string) {
  render(NewCustomReportPage, {});
  await page.getByText(question, { exact: true }).click();
  await expect.element(page.getByTestId("report-workspace")).toBeVisible();
  await vi.waitFor(() => expect(calls.runs.length).toBeGreaterThan(0));
}

async function save(name: string) {
  await page.getByTestId("save-trigger").click();
  await page.getByLabelText("Name", { exact: true }).fill(name);
  await page.getByRole("button", { name: "Save the report" }).click();
}

describe("a new custom report", () => {
  beforeEach(async () => {
    calls.runs.length = 0;
    calls.creates.length = 0;
    calls.gotos.length = 0;
    refusal = null;
    pageState.data = {};
    await page.viewport(1280, 900);
  });

  it("starts with the questions, grouped, and a blank report", async () => {
    render(NewCustomReportPage, {});

    await expect.element(page.getByRole("heading", { name: "Start with a question" })).toBeVisible();
    await expect.element(page.getByRole("heading", { name: "Lows and highs" })).toBeVisible();
    await expect.element(page.getByText("Is there a dawn rise?", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Blank report", { exact: true })).toBeVisible();
    expect(calls.runs).toEqual([]);
  });

  it("opens a question in the workspace and previews its chart", async () => {
    await start("Is there a dawn rise?");

    expect(layerIds(calls.runs[0].spec)).toEqual(["glucoseWide", "glucose", "target"]);
    await expect.element(page.getByTestId("chart-caption")).toHaveTextContent("over a typical day");
    await expect.element(page.getByTestId("chart-preview").getByTestId("report-layered-chart")).toBeVisible();
    // The chips name the layers, so the chart's own legend is left out.
    expect(page.getByTestId("chart-preview").getByTestId("report-legend").elements()).toHaveLength(0);
  });

  it("runs the preview again when a signal is taken off the chart", async () => {
    await start("Is there a dawn rise?");
    const before = calls.runs.length;

    await page.getByTestId("signal-rail").getByRole("button", { name: "Target range" }).click();

    await vi.waitFor(() => expect(calls.runs.length).toBeGreaterThan(before), { timeout: 3000 });
    expect(layerIds(calls.runs.at(-1)!.spec)).toEqual(["glucoseWide", "glucose"]);
  });

  it("disables a control that cannot be used now, and says why beside it", async () => {
    await start("Is there a dawn rise?");
    await page.getByRole("radio", { name: "One against another" }).click();
    const rail = page.getByTestId("signal-rail");
    await rail.getByRole("button", { name: "Insulin", exact: true }).click();
    await rail.getByRole("button", { name: "Insulin on board" }).click();
    await rail.getByRole("button", { name: "Food", exact: true }).click();

    const reason = page.getByText("This view shows two signals. Remove one first.").first();
    await expect.element(reason).toBeVisible();
    const id = reason.element().id;
    const blocked = rail.getByRole("button", { name: "Carbs on board" });
    await expect.element(blocked).toBeDisabled();
    expect(blocked.element().getAttribute("aria-describedby")).toBe(id);
  });

  it("saves the spec with the workspace's state, then opens the saved report", async () => {
    await start("Is there a dawn rise?");
    await save("Dawn");

    await vi.waitFor(() => expect(calls.creates).toHaveLength(1));
    const { name, spec } = calls.creates[0];
    expect(name).toBe("Dawn");
    expect(layerIds(spec)).toEqual(["glucoseWide", "glucose", "target"]);
    expect(spec.builder).toMatchObject({ kind: BUILDER_KIND, version: BUILDER_VERSION, state: { rangeHours: 336 } });
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom/new-id"]));
  });

  it("shows the engine's refusal beside Save, and stays", async () => {
    refusal = { status: 400, body: { message: "The builder state is too large. It can be at most 32 kB." } };
    await start("Is there a dawn rise?");
    await save("Dawn");

    await expect.element(page.getByRole("alert")).toHaveTextContent("The builder state is too large.");
    expect(calls.gotos).toEqual([]);
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
  });

  it("moves the report to the form after a confirm, and saves it from there without the state", async () => {
    await start("Is there a dawn rise?");

    await page.getByRole("button", { name: "All settings" }).click();
    await expect.element(page.getByTestId("report-spec")).toHaveTextContent('"glucoseWide"');
    await page.getByTestId("change-in-form").click();
    await expect.element(page.getByRole("alertdialog")).toHaveTextContent("it cannot open in the question builder again");
    await page.getByRole("alertdialog").getByRole("button", { name: "Change in the form" }).click();

    await expect.element(page.getByTestId("report-workspace")).not.toBeInTheDocument();
    await expect.element(page.getByTestId("track-section").first()).toBeVisible();
    await page.getByLabelText("Name", { exact: true }).fill("Dawn in the form");
    await page.getByRole("button", { name: "Save", exact: true }).click();

    await vi.waitFor(() => expect(calls.creates).toHaveLength(1));
    expect(calls.creates[0].name).toBe("Dawn in the form");
    expect(calls.creates[0].spec.builder).toBeUndefined();
  });

  it("goes back to the questions with Start again", async () => {
    await start("Is there a dawn rise?");

    await page.getByRole("button", { name: "Start again" }).click();

    await expect.element(page.getByRole("heading", { name: "Start with a question" })).toBeVisible();
  });

  it("tells a member limited to the last 24 hours that it cannot create reports", async () => {
    pageState.data = { limitTo24Hours: true };
    render(NewCustomReportPage, {});

    await expect.element(page.getByTestId("clamped-note")).toHaveTextContent("Your access is limited to the last 24 hours. It cannot create reports.");
    expect(page.getByTestId("report-starters").elements()).toHaveLength(0);
  });
});
