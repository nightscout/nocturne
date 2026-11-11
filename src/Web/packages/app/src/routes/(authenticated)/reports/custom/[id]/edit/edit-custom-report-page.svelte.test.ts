import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { CustomReport, CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";
import { catalogue, resultFor, statisticsTemplate } from "#lib/components/reports/custom/builder/schema-form.fixtures.js";
import { runOf, statTilesResult } from "../../custom-report-fixtures.js";
import { liveCatalogue } from "#lib/components/reports/custom/workspace/catalogue.fixtures.js";
import { minuteLabel } from "#lib/components/reports/custom/workspace/clock.js";
import { Report } from "#lib/components/reports/custom/workspace/report.svelte.js";
import { BUILDER_KIND, withBuilder } from "#lib/components/reports/custom/workspace/saved-state.js";
import { starter } from "#lib/components/reports/custom/workspace/starters.js";

const calls = vi.hoisted(() => ({ runs: [] as unknown[], validations: [] as unknown[], updates: [] as unknown[], creates: [] as unknown[], gotos: [] as string[] }));
/** How the validator answers: as the engine does, or by failing to answer, as a network error does. */
let validatorDown = false;
let saved: CustomReport;
/** The catalogue the page reads: the form's fixture, or a live one for a report made in the workspace. */
let known: CustomReportCatalogue = catalogue;

vi.mock("#lib/api/report-image.remote.js", () => ({ drawReportPanelImage: () => Promise.resolve("") }));

vi.mock("#lib/api/generated/customReports.generated.remote.js", () => ({
  getCustomReport: () => remoteQuery(() => saved),
  getCustomReportCatalogue: () => remoteQuery(() => known),
  getCustomReports: () => remoteQuery(() => []),
  runCustomReport: remoteCommand((arg) => {
    calls.runs.push(arg);
    const spec = (arg as { spec: Record<string, unknown> }).spec;
    return known === catalogue ? { result: resultFor(spec, statTilesResult()), spec, errors: [] } : runOf(spec);
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
  deleteCustomReport: remoteCommand(() => undefined),
}));

vi.mock("$app/navigation", () => ({
  goto: (url: string) => {
    calls.gotos.push(url);
    return Promise.resolve();
  },
}));

import EditCustomReportPage from "./+page.svelte";

describe("editing a saved custom report", () => {
  beforeEach(() => {
    calls.runs.length = 0;
    calls.updates.length = 0;
    calls.creates.length = 0;
    calls.gotos.length = 0;
    pageState.params = { id: "r1" };
    saved = { id: "r1", name: "Fortnight", spec: statisticsTemplate.spec, errors: [], canEdit: true };
    known = catalogue;
    pageState.data = {};
  });

  it("opens a report made in the form in the form, and says why", async () => {
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("opened-in-form")).toHaveTextContent("not made in this builder, or it was changed outside it");
    await expect.element(page.getByLabelText("Title", { exact: true })).toHaveValue("Two weeks");
    expect(page.getByTestId("report-workspace").elements()).toHaveLength(0);
  });

  it("keeps a member limited to the last 24 hours in the form, unable to change or copy", async () => {
    pageState.data = { limitTo24Hours: true };
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("clamped-note")).toBeVisible();
    await expect.element(page.getByLabelText("Title", { exact: true })).toHaveValue("Two weeks");
    expect(page.getByRole("button", { name: "Save", exact: true }).elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Save as" }).elements()).toHaveLength(0);
  });

  it("opens the saved spec, and saves a change to it and its name", async () => {
    render(EditCustomReportPage, {});

    const title = page.getByLabelText("Title", { exact: true });
    await expect.element(title).toHaveValue("Two weeks");
    await expect.element(page.getByText("72.4")).toBeVisible();
    await title.fill("Three weeks");
    await page.getByLabelText("Name", { exact: true }).fill("Three weeks of figures");
    await page.getByRole("button", { name: "Save", exact: true }).click();

    await vi.waitFor(() => expect(calls.updates).toHaveLength(1));
    expect(calls.updates[0]).toMatchObject({
      id: "r1",
      request: { name: "Three weeks of figures", spec: { title: "Three weeks", panels: [{ id: "p1", dataset: { name: "glucose_statistics" } }] } },
    });
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom/r1"]));
  });

  it("lets a member who cannot change the report save a copy only", async () => {
    saved = { ...saved, canEdit: false };
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("read-only-note")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Save", exact: true })).not.toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Delete" })).not.toBeInTheDocument();

    await page.getByRole("button", { name: "Save as" }).click();
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

  beforeEach(async () => {
    calls.runs.length = 0;
    calls.validations.length = 0;
    validatorDown = false;
    calls.updates.length = 0;
    calls.creates.length = 0;
    calls.gotos.length = 0;
    pageState.params = { id: "r1" };
    pageState.data = {};
    known = liveCatalogue;
    await page.viewport(1280, 900);
  });

  it("reopens it in the workspace, and saves a change with the state", async () => {
    saved = madeInWorkspace();
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    expect(page.getByTestId("opened-in-form").elements()).toHaveLength(0);
    await expect.element(page.getByTestId("chart-caption")).toHaveTextContent("over a typical day");

    await page.getByTestId("signal-rail").getByRole("button", { name: "Target range" }).click();
    await page.getByTestId("save-trigger").click();
    await expect.element(page.getByLabelText("Name", { exact: true })).toHaveValue("Dawn");
    await page.getByRole("button", { name: "Save the report" }).click();

    await vi.waitFor(() => expect(calls.updates).toHaveLength(1));
    const { request } = calls.updates[0] as { request: { name: string; spec: { builder: { kind: string }; panels: unknown[] } } };
    expect(request.name).toBe("Dawn");
    expect(request.spec.builder.kind).toBe(BUILDER_KIND);
    expect(JSON.stringify(request.spec.panels)).not.toContain(`"id":"target"`);
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom/r1"]));
  });

  it("opens it in the form when the spec was changed outside the workspace", async () => {
    saved = madeInWorkspace((spec) => ({ ...spec, title: "Changed elsewhere" }));
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("opened-in-form")).toBeVisible();
    await expect.element(page.getByLabelText("Title", { exact: true })).toHaveValue("Changed elsewhere");
    expect(page.getByTestId("report-workspace").elements()).toHaveLength(0);
  });

  it("moves it to the form after a confirm, with the spec as it is now", async () => {
    saved = madeInWorkspace();
    render(EditCustomReportPage, {});

    await page.getByRole("button", { name: "All settings" }).click();
    await page.getByTestId("change-in-form").click();
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("it cannot open in this builder again");
    await dialog.getByRole("button", { name: "Change in the form" }).click();

    await expect.element(page.getByTestId("track-section").first()).toBeVisible();
    expect(page.getByTestId("opened-in-form").elements()).toHaveLength(0);
    await page.getByRole("button", { name: "Save", exact: true }).click();
    await vi.waitFor(() => expect(calls.updates).toHaveLength(1));
    expect((calls.updates[0] as { request: { spec: Record<string, unknown> } }).request.spec).not.toHaveProperty("builder");
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
    expect(page.getByTestId("panel-card").elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Save", exact: true }).elements()).toHaveLength(0);

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

  it("asks before a save from the form removes the state of the builder it was made in", async () => {
    saved = madeInWorkspace((spec) => ({ ...spec, title: "Changed elsewhere" }));
    render(EditCustomReportPage, {});
    await expect.element(page.getByTestId("opened-in-form")).toBeVisible();

    await page.getByRole("button", { name: "Save", exact: true }).click();
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("Saving from the form removes those settings");
    expect(calls.updates).toEqual([]);
    await dialog.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(calls.updates).toHaveLength(1));
    expect((calls.updates[0] as { request: { spec: Record<string, unknown> } }).request.spec).not.toHaveProperty("builder");
  });

  it("asks nothing before a save from the form of a report made in the form", async () => {
    saved = { id: "r1", name: "Fortnight", spec: statisticsTemplate.spec, errors: [], canEdit: true };
    known = catalogue;
    render(EditCustomReportPage, {});
    await expect.element(page.getByTestId("opened-in-form")).toBeVisible();

    await page.getByRole("button", { name: "Save", exact: true }).click();

    await vi.waitFor(() => expect(calls.updates).toHaveLength(1));
    expect(page.getByRole("alertdialog").elements()).toHaveLength(0);
  });
});
