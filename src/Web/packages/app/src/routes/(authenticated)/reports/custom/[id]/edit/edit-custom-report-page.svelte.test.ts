import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { CustomReport } from "#lib/api/generated/nocturne-api-client.js";
import { chartTemplate, runOf, statisticsTemplate } from "../../custom-report-fixtures.js";
import { liveCatalogue } from "#lib/components/reports/custom/workspace/catalogue.fixtures.js";
import { minuteLabel } from "#lib/components/reports/custom/workspace/clock.js";
import { Report } from "#lib/components/reports/custom/workspace/report.svelte.js";
import { BUILDER_KIND, withBuilder } from "#lib/components/reports/custom/workspace/saved-state.js";
import { starter } from "#lib/components/reports/custom/workspace/starters.js";

const calls = vi.hoisted(() => ({ runs: [] as unknown[], validations: [] as unknown[], updates: [] as unknown[], creates: [] as unknown[], gotos: [] as string[] }));
/** How the validator answers: as the engine does, or by failing to answer, as a network error does. */
let validatorDown = false;
let saved: CustomReport;

type Saved = { request: { name: string; spec: { builder?: { kind: string }; title?: string; panels: Record<string, unknown>[] } } };

vi.mock("#lib/api/report-image.remote.js", () => ({ drawReportPanelImage: () => Promise.resolve("") }));

vi.mock("#lib/api/generated/customReports.generated.remote.js", () => ({
  getCustomReport: () => remoteQuery(() => saved),
  getCustomReportCatalogue: () => remoteQuery(() => liveCatalogue),
  getCustomReportSignalAvailability: () => remoteQuery(() => ({ hours: 336, signals: [] })),
  getCustomReports: () => remoteQuery(() => []),
  runCustomReport: remoteCommand((arg) => {
    calls.runs.push(arg);
    return runOf((arg as { spec: Record<string, unknown> }).spec);
  }),
  validateCustomReport: remoteCommand((arg) => {
    calls.validations.push(arg);
    if (validatorDown) return Promise.reject(new Error("The report engine did not answer."));
    return { spec: (arg as { spec: unknown }).spec, errors: [] };
  }),
  updateCustomReport: remoteCommand((arg) => {
    calls.updates.push(arg);
    return saved;
  }),
  createCustomReport: remoteCommand((arg) => {
    calls.creates.push(arg);
    return { id: "copy-id" };
  }),
}));

vi.mock("$app/navigation", () => ({
  goto: (url: string) => {
    calls.gotos.push(url);
    return Promise.resolve();
  },
}));

import EditCustomReportPage from "./+page.svelte";

async function saveAs(name: string) {
  await page.getByTestId("save-trigger").click();
  await page.getByLabelText("Name", { exact: true }).fill(name);
  await page.getByRole("button", { name: "Save the report" }).click();
}

beforeEach(async () => {
  calls.runs.length = 0;
  calls.validations.length = 0;
  calls.updates.length = 0;
  calls.creates.length = 0;
  calls.gotos.length = 0;
  validatorDown = false;
  pageState.params = { id: "r1" };
  pageState.data = {};
  await page.viewport(1280, 900);
});

describe("editing a report saved without the workspace's state", () => {
  beforeEach(() => {
    saved = { id: "r1", name: "Fortnight", spec: statisticsTemplate.spec, errors: [], canEdit: true };
  });

  it("opens it in the workspace, a panel of figures as a block of figures", async () => {
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    await expect.element(page.getByLabelText("Report title")).toHaveValue("Two weeks");
    await expect.element(page.getByTestId("block-editor-figures")).toBeVisible();
    expect(page.getByTestId("change-in-form").elements()).toHaveLength(0);
  });

  it("saves the spec it opened, with the workspace's state, and its name", async () => {
    render(EditCustomReportPage, {});
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    await page.getByLabelText("Report title").fill("Three weeks");
    await saveAs("Three weeks of figures");

    await vi.waitFor(() => expect(calls.updates).toHaveLength(1));
    const { request } = calls.updates[0] as Saved;
    expect(request.name).toBe("Three weeks of figures");
    expect(request.spec).toMatchObject({
      title: "Three weeks",
      panels: [{ id: "p1", kind: "stat_tiles", dataset: { name: "glucose_statistics" }, bindings: { columns: ["timeInRangePercent", "mean", "tdd"] }, range: { hours: 336 } }],
    });
    expect(request.spec.builder?.kind).toBe(BUILDER_KIND);
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom/r1"]));
  });

  it("opens a chart signal by signal, changes one in the advanced settings, and saves the rest as it was", async () => {
    saved = { id: "r1", name: "Glucose", spec: chartTemplate.spec, errors: [], canEdit: true };
    render(EditCustomReportPage, {});

    const rail = page.getByTestId("signal-rail");
    await expect.element(rail.getByText("Glucose", { exact: true })).toBeVisible();
    await expect.element(rail.getByText("IOB", { exact: true })).toBeVisible();
    await page.getByTestId("block-advanced").click();
    const iob = page.getByTestId("given-advanced").getByRole("listitem").nth(1);
    await iob.getByRole("checkbox", { name: "Own scale" }).click();
    await iob.getByLabelText("Name in the legend").fill("Insulin on board");
    await iob.getByLabelText("Name in the legend").element().dispatchEvent(new Event("change", { bubbles: true }));
    await expect.element(rail.getByText("Insulin on board", { exact: true })).toBeVisible();
    await saveAs("Glucose");

    await vi.waitFor(() => expect(calls.updates).toHaveLength(1));
    const [panel] = (calls.updates[0] as Saved).request.spec.panels;
    expect(panel).toMatchObject({
      id: "glucose",
      tracks: [
        { id: "glucose", height: 2, layers: [{ id: "glucose", dataset: { name: "glucose_series" }, mark: "line" }, { id: "iob", title: "Insulin on board" }] },
      ],
    });
  });

  it("keeps a member limited to the last 24 hours from changing or copying it", async () => {
    pageState.data = { limitTo24Hours: true };
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("clamped-note")).toBeVisible();
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    expect(page.getByTestId("save-trigger").elements()).toHaveLength(0);
    expect(page.getByTestId("save-copy-trigger").elements()).toHaveLength(0);
  });

  it("lets a member who cannot change the report save a copy only", async () => {
    saved = { ...saved, canEdit: false };
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("read-only-note")).toBeVisible();
    await expect.element(page.getByTestId("save-copy-trigger")).toBeVisible();
    expect(page.getByTestId("save-trigger").elements()).toHaveLength(0);

    await page.getByTestId("save-copy-trigger").click();
    await page.getByLabelText("Name of the copy").fill("My copy");
    await page.getByRole("button", { name: "Save the copy" }).click();

    await vi.waitFor(() => expect(calls.creates).toHaveLength(1));
    expect(calls.creates[0]).toMatchObject({ name: "My copy", spec: { title: "Two weeks" } });
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom/copy-id"]));
    expect(calls.updates).toEqual([]);
  });
});

describe("editing a report made in the workspace", () => {
  /** A saved report as the API returns one the workspace made: the spec it stored, and the state with it. */
  function madeInWorkspace(change: (spec: Record<string, unknown>) => Record<string, unknown> = (spec) => spec): CustomReport {
    const report = new Report(liveCatalogue, starter("dawn-rise")!, minuteLabel);
    return { id: "r1", name: "Dawn", spec: withBuilder(change(report.spec()), report.toState()), errors: [], canEdit: true };
  }

  it("reopens it from its state, and saves a change with the state", async () => {
    saved = madeInWorkspace();
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    await expect.element(page.getByTestId("chart-caption")).toHaveTextContent("over a typical day");

    await page.getByTestId("signal-rail").getByRole("button", { name: "Remove Target range" }).click();
    await page.getByTestId("save-trigger").click();
    await expect.element(page.getByLabelText("Name", { exact: true })).toHaveValue("Dawn");
    await page.getByRole("button", { name: "Save the report" }).click();

    await vi.waitFor(() => expect(calls.updates).toHaveLength(1));
    const { request } = calls.updates[0] as Saved;
    expect(request.name).toBe("Dawn");
    expect(request.spec.builder?.kind).toBe(BUILDER_KIND);
    expect(JSON.stringify(request.spec.panels)).not.toContain(`"id":"target"`);
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom/r1"]));
  });

  it("opens it from the spec when the spec was changed elsewhere, and saves the change kept", async () => {
    saved = madeInWorkspace((spec) => ({ ...spec, title: "Changed elsewhere" }));
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    await expect.element(page.getByLabelText("Report title")).toHaveValue("Changed elsewhere");
    await expect.element(page.getByTestId("given-workspace")).toBeVisible();
    await saveAs("Dawn");

    await vi.waitFor(() => expect(calls.updates).toHaveLength(1));
    expect((calls.updates[0] as Saved).request.spec.title).toBe("Changed elsewhere");
  });

  it("changes a chart of signals signal by signal after a confirm, keeping what it shows", async () => {
    saved = madeInWorkspace();
    render(EditCustomReportPage, {});

    await page.getByTestId("block-advanced").click();
    await page.getByTestId("signal-by-signal").click();
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("you cannot change its view or its groups");
    await dialog.getByRole("button", { name: "Change signal by signal" }).click();

    await expect.element(page.getByTestId("given-workspace")).toBeVisible();
    await expect.element(page.getByRole("radio", { name: "Typical day" })).not.toBeInTheDocument();
  });

  it("lets a member who cannot change it save a copy with the state", async () => {
    saved = { ...madeInWorkspace(), canEdit: false };
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    expect(page.getByTestId("save-trigger").elements()).toHaveLength(0);
    await page.getByTestId("save-copy-trigger").click();
    await page.getByLabelText("Name of the copy").fill("My dawn");
    await page.getByRole("button", { name: "Save the copy" }).click();

    await vi.waitFor(() => expect(calls.creates).toHaveLength(1));
    expect(calls.creates[0]).toMatchObject({ name: "My dawn", spec: { builder: { kind: BUILDER_KIND } } });
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom/copy-id"]));
  });

  it("says it could not check how the report opens, opens nothing, and tries again", async () => {
    saved = madeInWorkspace();
    validatorDown = true;
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("opening-failed")).toHaveTextContent("Could not check how this report opens");
    expect(page.getByTestId("report-workspace").elements()).toHaveLength(0);

    validatorDown = false;
    await page.getByRole("button", { name: "Try again" }).click();
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    expect(calls.updates).toEqual([]);
  });

  it("checks with the validator, which reads no data, not with a run", async () => {
    saved = madeInWorkspace();
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    expect(calls.validations).toHaveLength(1);
    expect((calls.validations[0] as { spec: Record<string, unknown> }).spec).not.toHaveProperty("builder");
  });
});
