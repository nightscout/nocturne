import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { CustomReport } from "@nocturne/api-client";
import { chartTemplate, runOf, statisticsTemplate } from "../../custom-report-fixtures.js";
import { liveCatalogue } from "#lib/components/reports/custom/workspace/catalogue.fixtures.js";
import { minuteLabel } from "#lib/components/reports/custom/workspace/clock.js";
import { Report } from "#lib/components/reports/custom/workspace/report.svelte.js";
import { BUILDER_KIND, withBuilder } from "#lib/components/reports/custom/workspace/saved-state.js";
import { starter } from "#lib/components/reports/custom/workspace/starters.js";

const calls = vi.hoisted(() => ({
  runs: [] as unknown[],
  validations: [] as unknown[],
  updates: [] as unknown[],
  creates: [] as unknown[],
  gotos: [] as string[],
  /** The leave guards the page registered, newest last. */
  guards: [] as ((navigation: { type: string; to: { url: URL } | null; cancel: () => void }) => void)[],
}));
/** How the validator answers: as the engine does, or by failing to answer, as a network error does. */
let validatorDown = false;
/** The saved report another member saves before the next update arrives, which the API then refuses. */
let savedElsewhere: CustomReport | null = null;
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
    const expected = (arg as { request: { expectedUpdatedAt?: string } }).request.expectedUpdatedAt;
    if (savedElsewhere) {
      saved = savedElsewhere;
      savedElsewhere = null;
    }
    if (expected !== undefined && expected !== saved.updatedAt)
      return Promise.reject({ status: 409, body: { message: "The report was changed.", code: "custom_report_changed" } });
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
  beforeNavigate: (guard: (typeof calls.guards)[number]) => calls.guards.push(guard),
}));

import EditCustomReportPage from "./+page.svelte";

/** The page data of a signed-in member of a tenant, whose drafts this browser keeps. */
const MEMBER = { tenantSlug: "dev", user: { subjectId: "subject-1" } };

/** A saved report as the API returns one the workspace made: the spec it stored, and the state with it. */
function madeInWorkspace(change: (spec: Record<string, unknown>) => Record<string, unknown> = (spec) => spec): CustomReport {
  const report = new Report(liveCatalogue, starter("dawn-rise")!, minuteLabel);
  return { id: "r1", name: "Dawn", spec: withBuilder(change(report.spec()), report.toState()), errors: [], canEdit: true };
}

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
  calls.guards.length = 0;
  validatorDown = false;
  savedElsewhere = null;
  localStorage.clear();
  pageState.params = { id: "r1" };
  pageState.data = { ...MEMBER };
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

  it("adds a signal to a chart from the catalogue, which lists only the signals along its axis", async () => {
    saved = { id: "r1", name: "Glucose", spec: chartTemplate.spec, errors: [], canEdit: true };
    render(EditCustomReportPage, {});

    await page.getByTestId("signal-rail").getByTestId("add-signal").click();
    const dialog = page.getByRole("dialog");
    await expect.element(dialog.getByText("Search the signals this chart can show.")).toBeVisible();
    await dialog.getByTestId("catalogue-search").fill("Carbs on board");
    await dialog.getByRole("button", { name: "Carbs on board", exact: true }).click();
    // Time in range is a figure per day, not a value along time, so the timeline's catalogue does not offer it.
    await dialog.getByTestId("catalogue-search").fill("Time in range");
    await expect.element(dialog.getByTestId("signal-catalogue")).toBeVisible();
    expect(dialog.getByRole("button", { name: "Time in range", exact: true }).elements()).toHaveLength(0);
    await userEvent.keyboard("{Escape}");

    await expect.element(page.getByTestId("signal-rail").getByText("COB", { exact: true })).toBeVisible();
  });

  it("keeps a member limited to the last 24 hours from changing or copying it", async () => {
    pageState.data = { ...MEMBER, limitTo24Hours: true };
    render(EditCustomReportPage, {});

    await expect.element(page.getByTestId("clamped-note")).toBeVisible();
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    expect(page.getByTestId("save-trigger").elements()).toHaveLength(0);
    expect(page.getByTestId("save-copy-trigger").elements()).toHaveLength(0);
  });

  it("offers a handed-off session a sign-in in place of saving or copying", async () => {
    pageState.data = { ...MEMBER, isHandedOffSession: true };
    render(EditCustomReportPage, {});

    await expect.element(page.getByText("Sign in again to change or copy this report.")).toBeVisible();
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

describe("the unsaved changes to a saved report", () => {
  beforeEach(() => {
    saved = { id: "r1", name: "Fortnight", spec: statisticsTemplate.spec, errors: [], canEdit: true };
  });

  it("opens with the changes this browser kept, and puts the saved report back on Discard", async () => {
    render(EditCustomReportPage, {});
    const title = page.getByLabelText("Report title");
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    const input = title.element();
    const before = input instanceof HTMLInputElement ? input.value : "";
    await title.fill("Fortnight, unsaved");
    await vi.waitFor(() => expect(localStorage.length).toBe(1));

    document.body.innerHTML = "";
    render(EditCustomReportPage, {});
    await expect.element(page.getByText("Restored your unsaved changes.")).toBeVisible();
    await expect.element(page.getByLabelText("Report title")).toHaveValue("Fortnight, unsaved");

    await page.getByRole("button", { name: "Discard changes" }).click();
    await expect.element(page.getByLabelText("Report title")).toHaveValue(before);
    await expect.element(page.getByText("Restored your unsaved changes.")).not.toBeInTheDocument();
    expect(localStorage.length).toBe(0);
  });

  /** Asks the page's newest leave guard about closing the tab (no dialog), and says whether it held the page. */
  function guardHolds(): boolean {
    let held = false;
    calls.guards.at(-1)?.({ type: "leave", to: null, cancel: () => (held = true) });
    return held;
  }

  it("asks before it opens changes kept for a report that changed elsewhere since, and keeps them on request", async () => {
    saved = { ...madeInWorkspace(), updatedAt: "2026-11-16T01:00:00Z" };
    render(EditCustomReportPage, {});
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    await page.getByLabelText("Report title").fill("My unsaved title");
    await vi.waitFor(() => expect(localStorage.length).toBe(1));

    // Another member saves the report: its version on the server moves on.
    saved = { ...madeInWorkspace((spec) => ({ ...spec, title: "Their title" })), updatedAt: "2026-11-16T02:00:00Z" };
    document.body.innerHTML = "";
    render(EditCustomReportPage, {});
    await expect.element(page.getByTestId("draft-conflict")).toHaveTextContent("changed somewhere else");
    await expect.element(page.getByTestId("conflict-differences")).toHaveTextContent('The saved title is "Their title". Your title is "My unsaved title".');
    expect(page.getByTestId("report-workspace").elements()).toHaveLength(0);
    expect(page.getByText("Restored your unsaved changes.").elements()).toHaveLength(0);

    await page.getByRole("button", { name: "Keep my changes" }).click();
    await expect.element(page.getByLabelText("Report title")).toHaveValue("My unsaved title");
    await expect.element(page.getByText("Restored your unsaved changes.")).toBeVisible();
  });

  it("opens the report as it is now, and drops the old changes, on Discard my changes", async () => {
    saved = { ...madeInWorkspace(), updatedAt: "2026-11-16T01:00:00Z" };
    render(EditCustomReportPage, {});
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    await page.getByLabelText("Report title").fill("My unsaved title");
    await vi.waitFor(() => expect(localStorage.length).toBe(1));

    saved = { ...madeInWorkspace((spec) => ({ ...spec, title: "Their title" })), updatedAt: "2026-11-16T02:00:00Z" };
    document.body.innerHTML = "";
    render(EditCustomReportPage, {});
    await page.getByRole("button", { name: "Discard my changes" }).click();
    await expect.element(page.getByLabelText("Report title")).toHaveValue("Their title");
    expect(localStorage.length).toBe(0);
  });

  it("discards the changes on Cancel after a confirm, so leaving asks nothing and nothing is kept", async () => {
    saved = madeInWorkspace();
    render(EditCustomReportPage, {});
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    await page.getByLabelText("Report title").fill("Unsaved");
    await vi.waitFor(() => expect(localStorage.length).toBe(1));
    expect(guardHolds()).toBe(true);

    await page.getByRole("button", { name: "Cancel", exact: true }).click();
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("Discard your changes?");
    expect(calls.gotos).toEqual([]);
    await dialog.getByRole("button", { name: "Discard changes" }).click();

    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom/r1"]));
    expect(localStorage.length).toBe(0);
    expect(guardHolds()).toBe(false);
  });

  it("leaves on Cancel at once when nothing changed", async () => {
    saved = madeInWorkspace();
    render(EditCustomReportPage, {});
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    await page.getByRole("button", { name: "Cancel", exact: true }).click();
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom/r1"]));
    expect(page.getByRole("alertdialog").elements()).toHaveLength(0);
  });

  it("keeps the draft under the tenant and the member", async () => {
    saved = madeInWorkspace();
    render(EditCustomReportPage, {});
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    await page.getByLabelText("Report title").fill("Unsaved");

    await vi.waitFor(() => expect(localStorage.length).toBe(1));
    const key = localStorage.key(0) ?? "";
    expect(key).toContain(".dev.");
    expect(key).toContain(".subject-1.");
    expect(key.endsWith(".r1")).toBe(true);
  });

  /** Asks the page's newest leave guard about a link in the app, which opens the leave dialog. */
  function leaveByLink() {
    calls.guards.at(-1)?.({ type: "link", to: { url: new URL("http://localhost/reports") }, cancel: () => {} });
  }

  for (const [who, data] of [
    ["a page with no member", { tenantSlug: "dev" }],
    ["a guest session", { tenantSlug: "dev", user: { subjectId: "guest" }, isGuestSession: true }],
  ] as const) {
    it(`keeps no draft for ${who}, and says so before it leaves`, async () => {
      pageState.data = data;
      saved = madeInWorkspace();
      render(EditCustomReportPage, {});
      await expect.element(page.getByTestId("report-workspace")).toBeVisible();
      await page.getByLabelText("Report title").fill("Unsaved");
      await expect.element(page.getByLabelText("Report title")).toHaveValue("Unsaved");

      expect(localStorage.length).toBe(0);
      expect(guardHolds()).toBe(true);
      leaveByLink();
      await expect.element(page.getByRole("alertdialog")).toHaveTextContent("only for a signed-in member");
    });
  }
});

describe("a save refused because the report changed somewhere else", () => {
  beforeEach(() => {
    saved = { ...madeInWorkspace(), updatedAt: "2026-11-16T01:00:00Z" };
    savedElsewhere = { ...madeInWorkspace((spec) => ({ ...spec, title: "Their title" })), name: "Their name", updatedAt: "2026-11-16T02:00:00Z" };
  });

  it("sends the version it opened, keeps the member's work on screen, and says what differs", async () => {
    render(EditCustomReportPage, {});
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    await page.getByLabelText("Report title").fill("My title");
    await saveAs("Dawn");

    await vi.waitFor(() => expect(calls.updates).toHaveLength(1));
    expect((calls.updates[0] as { request: { expectedUpdatedAt?: string } }).request.expectedUpdatedAt).toBe("2026-11-16T01:00:00Z");
    const conflict = page.getByTestId("draft-conflict");
    await expect.element(conflict).toHaveTextContent("did not save your changes");
    const differences = page.getByTestId("conflict-differences");
    await expect.element(differences).toHaveTextContent('The saved title is "Their title". Your title is "My title".');
    await expect.element(differences).toHaveTextContent('The saved name is "Their name". Your name is "Dawn".');
    await expect.element(page.getByLabelText("Report title")).toHaveValue("My title");
    expect(localStorage.length).toBe(1);
    expect(calls.gotos).toEqual([]);
  });

  it("saves over the other version only after a confirm that names it", async () => {
    render(EditCustomReportPage, {});
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    await page.getByLabelText("Report title").fill("My title");
    await saveAs("Dawn");
    await expect.element(page.getByTestId("draft-conflict")).toBeVisible();
    await userEvent.keyboard("{Escape}");

    await page.getByRole("button", { name: "Save my changes" }).click();
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent('replace the version of "Their name"');
    expect(calls.updates).toHaveLength(1);
    await dialog.getByRole("button", { name: "Replace", exact: true }).click();

    await vi.waitFor(() => expect(calls.updates).toHaveLength(2));
    const { request } = calls.updates[1] as { request: { expectedUpdatedAt?: string; spec: { title?: string } } };
    expect(request.expectedUpdatedAt).toBeUndefined();
    expect(request.spec.title).toBe("My title");
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom/r1"]));
    expect(localStorage.length).toBe(0);
  });

  it("opens the report as it is now on Discard my changes", async () => {
    render(EditCustomReportPage, {});
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    await page.getByLabelText("Report title").fill("My title");
    await saveAs("Dawn");
    await expect.element(page.getByTestId("draft-conflict")).toBeVisible();
    await userEvent.keyboard("{Escape}");

    await page.getByRole("button", { name: "Discard my changes" }).click();

    await expect.element(page.getByLabelText("Report title")).toHaveValue("Their title");
    expect(page.getByTestId("draft-conflict").elements()).toHaveLength(0);
    expect(localStorage.length).toBe(0);
  });
});
