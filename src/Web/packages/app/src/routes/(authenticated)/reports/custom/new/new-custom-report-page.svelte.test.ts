import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import { liveCatalogue } from "#lib/components/reports/custom/workspace/catalogue.fixtures.js";
import { BUILDER_KIND, BUILDER_VERSION } from "#lib/components/reports/custom/workspace/saved-state.js";
import { runOf } from "../custom-report-fixtures.js";
import { statisticsTemplate } from "#lib/components/reports/custom/builder/schema-form.fixtures.js";

type Spec = { panels: { id: string; tracks?: { layers: { id: string }[] }[] }[]; builder?: { kind: string; version: number; state: unknown } };

const calls = vi.hoisted(() => ({ runs: [] as { spec: Spec }[], creates: [] as { name: string; spec: Spec }[], gotos: [] as string[] }));
/** The engine's refusal of the next save, or null to save it. */
let refusal: unknown = null;
/** Whether a preview run fails, as one does when the API cannot be reached. */
let runsFail = false;

vi.mock("#lib/api/report-image.remote.js", () => ({ drawReportPanelImage: () => Promise.resolve("") }));

vi.mock("#lib/api/generated/foods.generated.remote.js", () => ({
  getFoods: () => remoteQuery(() => []),
  getFood: () => remoteQuery(() => ({ id: "f", name: "Penne" })),
}));

vi.mock("#lib/api/generated/customReports.generated.remote.js", () => ({
  getCustomReportCatalogue: () => remoteQuery(() => liveCatalogue),
  getCustomReports: () => remoteQuery(() => []),
  getCustomReportTemplates: () => remoteQuery(() => [statisticsTemplate]),
  runCustomReport: remoteCommand((arg) => {
    const run = arg as { spec: Spec };
    calls.runs.push(run);
    if (runsFail) return Promise.reject(new Error("offline"));
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
    runsFail = false;
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
    await expect.element(page.getByRole("alertdialog")).toHaveTextContent("it cannot open in this builder again");
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

    const restart = page.getByTestId("report-workspace").getByRole("button", { name: "Start again" });
    await restart.click();
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("This report is not saved.");
    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();

    await restart.click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Start again" }).click();
    await expect.element(page.getByRole("heading", { name: "Start with a question" })).toBeVisible();
  });

  it("announces each settled preview once, and only for the open block", async () => {
    await start("Is there a dawn rise?");
    const spoken = () =>
      [...document.querySelectorAll("[data-testid='report-workspace'] [aria-live]")].map((e) => e.textContent?.trim()).filter(Boolean);
    await vi.waitFor(() => expect(spoken()).toEqual([expect.stringMatching(/^Chart updated\. .*typical day/)]));

    await page.getByRole("button", { name: "Add figures" }).click();
    await vi.waitFor(() => expect(spoken()).toEqual(["Figures updated."]));
    expect(document.querySelectorAll("[role='status']").length).toBe(0);
  });

  it("clears the spinner after a failed run and shows only the failure", async () => {
    await start("Is there a dawn rise?");
    runsFail = true;
    await page.getByTestId("signal-rail").getByRole("button", { name: "Target range" }).click();

    await expect.element(page.getByText("The preview could not be made.")).toBeVisible();
    await vi.waitFor(() => expect(document.querySelectorAll("[data-testid='chart-preview'] [aria-busy='true']").length).toBe(0));
    expect(document.querySelector("[data-testid='chart-preview'] .animate-spin")).toBeNull();
  });

  it("lists the API's ready-made reports last, and opens one in the form", async () => {
    render(NewCustomReportPage, {});
    const ready = page.getByTestId("ready-made-reports");
    await expect.element(ready.getByRole("heading", { name: "Ready-made reports" })).toBeVisible();

    await ready.getByText("Statistics", { exact: true }).click();

    await expect.element(page.getByTestId("panel-card").first()).toBeVisible();
    await expect.element(page.getByLabelText("Title", { exact: true })).toHaveValue("Two weeks");
  });

  it("disables a lens that cannot take the chart, with the reason beside the switch", async () => {
    await start("Is there a dawn rise?");
    await page.getByRole("radio", { name: "Weekdays and the weekend" }).or(page.getByRole("button", { name: "Weekdays and the weekend" })).first().click();
    await page.getByRole("radio", { name: "Timeline" }).click();
    const rail = page.getByTestId("signal-rail");
    for (const family of ["Insulin", "Food"]) await rail.getByRole("button", { name: family, exact: true }).click();
    for (const signal of ["Insulin on board", "Carbs on board", "Basal delivered", "Boluses", "Carbs"])
      await rail.getByRole("button", { name: signal, exact: true }).click();

    const typical = page.getByRole("radio", { name: "Typical day" });
    await expect.element(typical).toBeDisabled();
    const reason = page.getByTestId("lens-reason").first();
    await expect.element(reason).toHaveTextContent("Typical day:");
    expect(typical.element().getAttribute("aria-describedby")).toBe(reason.element().id);
  });

  it("offers no earlier period under a view of the events one by one, and says why once", async () => {
    await start("What does a later breakfast do?");
    await page.getByRole("radio", { name: "Spread" }).click();
    await page.getByTestId("chart-options-toggle").click();

    await expect.element(page.getByTestId("comparison-reason")).toHaveTextContent("show the events of the range only");
    await page.getByTestId("event-options").click();
    expect(page.getByRole("menuitem", { name: "Compare with an earlier period" }).elements()).toHaveLength(0);
  });

  it("disables Save while a block is unfinished, with the reason beside it", async () => {
    render(NewCustomReportPage, {});
    await page.getByText("Blank report", { exact: true }).click();

    await expect.element(page.getByTestId("save-trigger")).toBeDisabled();
    await expect.element(page.getByTestId("save-blocked")).toHaveTextContent("Add a signal or a block first.");
  });

  it("moves the focus to the chip before a removed signal", async () => {
    await start("Is there a dawn rise?");
    await page.getByTestId("signal-chips").getByRole("button", { name: /^Target range/ }).click();
    await page.getByRole("button", { name: "Remove from chart" }).click();

    await vi.waitFor(() => expect(document.activeElement?.getAttribute("data-signal-chip")).toBe("glucose"));
  });

  it("moves the focus to Narrow when a group's only narrowing is removed", async () => {
    await start("What does a later breakfast do?");
    const row = page.getByTestId("moment-row").first();
    await row.getByRole("button", { name: /^Remove time of day/ }).click();

    await vi.waitFor(() => expect(document.activeElement?.textContent?.trim()).toBe("Narrow"));
  });

  it("names the thumbs of a time of day, and says the window once a thumb is let go", async () => {
    await start("What does a later breakfast do?");
    await page.getByTestId("moment-row").first().getByRole("button", { name: /^Time of day:/ }).click();

    const sliders = page.getByRole("slider");
    await expect.element(sliders.nth(0)).toHaveAttribute("aria-label", "Start");
    await expect.element(sliders.nth(1)).toHaveAttribute("aria-label", "End");
    expect(sliders.nth(0).element().getAttribute("aria-valuetext")).toMatch(/6|06/);
  });


  it("tells a member limited to the last 24 hours that it cannot create reports", async () => {
    pageState.data = { limitTo24Hours: true };
    render(NewCustomReportPage, {});

    await expect.element(page.getByTestId("clamped-note")).toHaveTextContent("Your access is limited to the last 24 hours. It cannot create reports.");
    expect(page.getByTestId("report-starters").elements()).toHaveLength(0);
  });
});
