import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { createQueryResource, remoteCommand, remoteQuery, remoteQueryError, type RemoteQueryStub } from "#lib/test-stubs/remote-resource.js";
import type { CustomReport } from "@nocturne/api-client";
import { chartResult, normalisedSpec, statTilesResult } from "../custom-report-fixtures.js";
import { liveCatalogue } from "#lib/components/reports/custom/workspace/catalogue.fixtures.js";

const calls = vi.hoisted(() => ({
  runs: [] as unknown[],
  updates: [] as unknown[],
  deletes: [] as unknown[],
  gotos: [] as string[],
  copied: [] as string[],
  images: [] as unknown[],
}));
let saved: CustomReport;
let runResult: () => unknown = statTilesResult;
/** The saved report another member saves before the next update arrives, which the API then refuses. */
let savedElsewhere: CustomReport | null = null;
/** The report the API reads: `saved`, or none (404) once another member deleted it. */
let deleted = false;
/** How the patient's zone answers. */
let zone: () => RemoteQueryStub<{ timezone: string | null }> = () => remoteQuery(() => ({ timezone: null }));

vi.mock("#lib/api/generated/patientTimezones.generated.remote.js", () => ({
  getPatientTimezone: () => zone(),
}));

vi.mock("#lib/api/generated/customReports.generated.remote.js", () => ({
  getCustomReport: () => (deleted ? remoteQueryError({ status: 404, body: { message: "Not found" } }) : remoteQuery(() => saved)),
  getCustomReportCatalogue: () => remoteQuery(() => liveCatalogue),
  runCustomReport: remoteCommand((arg) => {
    calls.runs.push(arg);
    return { result: runResult(), spec: normalisedSpec, errors: [] };
  }),
  updateCustomReport: remoteCommand((arg) => {
    calls.updates.push(arg);
    const { request } = arg as { request: { name?: string; expectedUpdatedAt?: string } };
    if (savedElsewhere) {
      saved = savedElsewhere;
      savedElsewhere = null;
    }
    if (request.expectedUpdatedAt !== undefined && request.expectedUpdatedAt !== saved.updatedAt)
      return Promise.reject({ status: 409, body: { message: "The report was changed.", code: "custom_report_changed" } });
    saved = { ...saved, name: request.name ?? saved.name, updatedAt: "2026-11-16T03:00:00Z" };
    return saved;
  }),
  deleteCustomReport: remoteCommand((id) => {
    calls.deletes.push(id);
  }),
}));

const image = vi.hoisted(() => ({ refuse: null as string | null }));
vi.mock("#lib/api/report-image.remote.js", async () => {
  const { error } = await import("@sveltejs/kit");
  return {
    drawReportPanelImage: remoteCommand((arg) => {
      calls.images.push(arg);
      if (image.refuse) error(503, image.refuse);
      return "iVBORw0KGgo=";
    }),
  };
});

vi.mock("$app/navigation", () => ({
  goto: (url: string) => {
    calls.gotos.push(url);
    return Promise.resolve();
  },
}));

vi.mock("#lib/utils/index.js", async (original) => ({
  ...(await original<typeof import("#lib/utils/index.js")>()),
  copyToClipboard: async (text: string) => {
    calls.copied.push(text);
    return true;
  },
}));

import CustomReportPage from "./+page.svelte";

/** The page data of a signed-in member of a tenant, whose drafts this browser keeps. */
const MEMBER = { tenantSlug: "dev", user: { subjectId: "subject-1" } };
const DRAFT_KEY = "nocturne.member.custom-report-draft.dev.subject-1.a1";

/** Picks the first and third days the date picker offers. */
async function pickDates() {
  await page.getByRole("button", { name: /^Dates to show/ }).click();
  await vi.waitFor(() => expect(document.querySelectorAll("[data-bits-day]").length).toBeGreaterThan(0));
  const days = [...document.querySelectorAll<HTMLElement>("[data-bits-day]")].filter(
    (d) => !d.hasAttribute("data-disabled") && !d.hasAttribute("data-outside-month")
  );
  days[0].click();
  days[2].click();
}

describe("saved custom report page", () => {
  beforeEach(() => {
    for (const list of Object.values(calls)) list.length = 0;
    pageState.params = { id: "a1" };
    pageState.url = new URL("http://localhost/reports/custom/a1") as typeof pageState.url;
    saved = { id: "a1", name: "Fortnight", spec: normalisedSpec, errors: [], canEdit: true };
    runResult = statTilesResult;
    image.refuse = null;
    savedElsewhere = null;
    deleted = false;
    zone = () => remoteQuery(() => ({ timezone: null }));
    pageState.data = { ...MEMBER };
    localStorage.clear();
  });

  it("says in the page's words why an image was refused", async () => {
    runResult = chartResult;
    image.refuse = "busy";
    render(CustomReportPage, {});

    await page.getByRole("button", { name: "Download PNG" }).click();

    await expect.element(page.getByRole("alert")).toHaveTextContent("The image renderer is busy. Try again.");
  });

  it("saves a chart panel as the PNG the image renderer draws, sending the spec to run again and the panel's id", async () => {
    runResult = chartResult;
    const created: Blob[] = [];
    const createObjectURL = vi.spyOn(URL, "createObjectURL").mockImplementation((blob) => {
      created.push(blob as Blob);
      return "blob:report";
    });
    vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
    const clicked: string[] = [];
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      clicked.push(this.download);
    });
    try {
      render(CustomReportPage, {});

      const download = page.getByRole("button", { name: "Download PNG" });
      await expect.element(download).toBeVisible();
      expect(download.elements(), "stat tiles have no image").toHaveLength(1);
      await download.click();

      await vi.waitFor(() => expect(clicked).toEqual(["glucose-and-target-range.png"]));
      expect(calls.images).toHaveLength(1);
      expect(calls.images).toEqual([{ spec: normalisedSpec, panelId: "line" }]);
      expect(created[0].type).toBe("image/png");
    } finally {
      createObjectURL.mockRestore();
      click.mockRestore();
    }
  });

  it("runs the saved spec as saved, with Edit and one menu of the other actions", async () => {
    render(CustomReportPage, {});

    await expect.element(page.getByText("Readings: 4025.")).toBeVisible();
    expect(calls.runs).toEqual([{ spec: normalisedSpec }]);
    await expect.element(page.getByRole("link", { name: "Edit" })).toBeVisible();
    await expect.element(page.getByRole("radio", { name: "As saved" })).toHaveAttribute("data-state", "on");
    await expect.element(page.getByTestId("report-actions")).toBeVisible();
  });

  it("runs the report over another range without changing what is saved", async () => {
    // As the API stores a report of 14 days: the report's range, and each panel's written out.
    const spec = { ...normalisedSpec, range: { hours: 336 } };
    saved = { ...saved, spec };
    render(CustomReportPage, {});
    await vi.waitFor(() => expect(calls.runs).toHaveLength(1));

    await page.getByRole("radio", { name: "7 days" }).click();

    await vi.waitFor(() => expect(calls.runs).toHaveLength(2));
    expect(calls.runs[1]).toEqual({ spec: { ...spec, range: { hours: 168 }, panels: [{ ...spec.panels[0], range: { hours: 168 } }] } });
    await expect.element(page.getByTestId("range-note")).toHaveTextContent("Nothing in the saved report changes.");
    expect(calls.updates).toEqual([]);
  });

  it("runs the report over two dates the reader picks, without changing what is saved", async () => {
    render(CustomReportPage, {});
    await vi.waitFor(() => expect(calls.runs).toHaveLength(1));

    await page.getByRole("button", { name: /^Dates to show/ }).click();
    await vi.waitFor(() => expect(document.querySelectorAll("[data-bits-day]").length).toBeGreaterThan(0));
    const days = [...document.querySelectorAll<HTMLElement>("[data-bits-day]")].filter(
      (d) => !d.hasAttribute("data-disabled") && !d.hasAttribute("data-outside-month")
    );
    days[0].click();
    days[2].click();

    await vi.waitFor(() => expect(calls.runs).toHaveLength(2));
    const run = calls.runs[1] as { spec: { range: { from?: string; to?: string } } };
    expect(Object.keys(run.spec.range)).toEqual(["from", "to"]);
    expect(Date.parse(run.spec.range.to!) - Date.parse(run.spec.range.from!)).toBeGreaterThanOrEqual(3 * 23 * 3_600_000);
    expect(calls.updates).toEqual([]);
  });

  it("renames, copies the link and deletes after the reader confirms", async () => {
    render(CustomReportPage, {});

    await page.getByTestId("report-actions").click();
    await page.getByRole("menuitem", { name: "Rename" }).click();
    await expect.element(page.getByLabelText("Name")).toHaveFocus();
    await page.getByLabelText("Name").fill("Two weeks of lunches");
    await page.getByRole("button", { name: "Save" }).click();
    await vi.waitFor(() => expect(calls.updates).toHaveLength(1));
    expect(calls.updates[0]).toMatchObject({ id: "a1", request: { name: "Two weeks of lunches" } });

    await page.getByTestId("report-actions").click();
    await page.getByRole("menuitem", { name: "Copy link" }).click();
    await vi.waitFor(() => expect(calls.copied).toEqual(["http://localhost/reports/custom/a1"]));

    await page.getByTestId("report-actions").click();
    await page.getByRole("menuitem", { name: "Delete" }).click();
    expect(calls.deletes).toEqual([]);
    await page.getByRole("alertdialog").getByRole("button", { name: "Delete" }).click();
    await vi.waitFor(() => expect(calls.deletes).toEqual(["a1"]));
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom"]));
  });

  it("offers no Rename or Delete when the API says the reader cannot change the report", async () => {
    saved = { ...saved, canEdit: false };
    render(CustomReportPage, {});

    await page.getByTestId("report-actions").click();
    await expect.element(page.getByRole("menuitem", { name: "Copy link" })).toBeVisible();
    expect(page.getByRole("menuitem", { name: "Rename" }).elements()).toHaveLength(0);
    expect(page.getByRole("menuitem", { name: "Delete" }).elements()).toHaveLength(0);
    await expect.element(page.getByRole("link", { name: "Edit a copy" })).toBeVisible();
  });

  it("on a handed-off session runs the report and offers a sign-in in place of Edit, Rename and Delete", async () => {
    pageState.data = { ...MEMBER, isHandedOffSession: true };
    render(CustomReportPage, {});

    await expect.element(page.getByText("Sign in again to change, copy, or delete this report.")).toBeVisible();
    expect(page.getByRole("link", { name: "Edit" }).elements()).toHaveLength(0);
    expect(page.getByRole("link", { name: "Edit a copy" }).elements()).toHaveLength(0);
    await page.getByTestId("report-actions").click();
    await expect.element(page.getByRole("menuitem", { name: "Copy link" })).toBeVisible();
    expect(page.getByRole("menuitem", { name: "Rename" }).elements()).toHaveLength(0);
    expect(page.getByRole("menuitem", { name: "Delete" }).elements()).toHaveLength(0);
  });

  it("shows why a stored spec no longer runs, and does not run it", async () => {
    saved = {
      id: "a1",
      name: "Old",
      spec: { version: 0, panels: [] },
      errors: [{ path: "version", message: "0 is not a supported version. Send the number 1." }],
      canEdit: true,
    };
    render(CustomReportPage, {});

    await expect.element(page.getByText("0 is not a supported version. Send the number 1.")).toBeVisible();
    await expect.element(page.getByText("Show the technical details")).toBeVisible();
    expect(calls.runs).toEqual([]);
  });

  async function renameTo(newName: string) {
    await page.getByTestId("report-actions").click();
    await page.getByRole("menuitem", { name: "Rename" }).click();
    await page.getByLabelText("Name").fill(newName);
    await page.getByRole("button", { name: "Save", exact: true }).click();
  }

  it("renames only the version it read, and keeps the member's own draft current", async () => {
    saved = { ...saved, updatedAt: "2026-11-16T01:00:00Z" };
    localStorage.setItem(DRAFT_KEY, JSON.stringify({ version: 1, savedAt: "2026-11-16T01:00:00Z", writtenAt: Date.now(), state: { title: "x" } }));
    render(CustomReportPage, {});

    await renameTo("Lunches");

    await vi.waitFor(() => expect(calls.updates).toHaveLength(1));
    expect(calls.updates[0]).toEqual({ id: "a1", request: { name: "Lunches", expectedUpdatedAt: "2026-11-16T01:00:00Z" } });
    await vi.waitFor(() => expect(JSON.parse(localStorage.getItem(DRAFT_KEY) ?? "{}").savedAt).toBe("2026-11-16T03:00:00Z"));
    expect(JSON.parse(localStorage.getItem(DRAFT_KEY) ?? "{}").state).toEqual({ title: "x" });
  });

  it("says a rename was refused when the report changed elsewhere, and renames it after a confirm", async () => {
    saved = { ...saved, updatedAt: "2026-11-16T01:00:00Z" };
    savedElsewhere = { ...saved, name: "Their name", updatedAt: "2026-11-16T02:00:00Z" };
    render(CustomReportPage, {});

    await renameTo("Lunches");

    await expect.element(page.getByTestId("rename-conflict")).toHaveTextContent('Its name is now "Their name"');
    await expect.element(page.getByLabelText("Name")).toHaveValue("Lunches");
    await page.getByRole("button", { name: "Rename it" }).click();
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent('The name "Lunches" replaces the name "Their name"');
    await dialog.getByRole("button", { name: "Replace", exact: true }).click();

    await vi.waitFor(() => expect(calls.updates).toHaveLength(2));
    expect(calls.updates[1]).toEqual({ id: "a1", request: { name: "Lunches" } });
    await expect.element(page.getByTestId("rename-conflict")).not.toBeInTheDocument();
  });

  it("clears the draft of a report that no longer exists", async () => {
    deleted = true;
    localStorage.setItem(DRAFT_KEY, "{}");
    render(CustomReportPage, {});

    await vi.waitFor(() => expect(localStorage.getItem(DRAFT_KEY)).toBeNull());
  });

  it("does not run two dates while the patient's zone loads", async () => {
    zone = () => createQueryResource(() => new Promise(() => {}));
    render(CustomReportPage, {});
    await vi.waitFor(() => expect(calls.runs).toHaveLength(1));

    await pickDates();

    await expect.element(page.getByTestId("zone-loading")).toBeVisible();
    expect(calls.runs).toHaveLength(1);
  });

  it("says it could not read the patient's zone, and runs the saved range rather than the dates in UTC", async () => {
    zone = () => remoteQueryError({ status: 500, body: { message: "down" } });
    render(CustomReportPage, {});
    await vi.waitFor(() => expect(calls.runs).toHaveLength(1));

    await pickDates();

    await expect.element(page.getByTestId("zone-failed")).toHaveTextContent("could not read the patient's time zone");
    expect(calls.runs.every((r) => !("from" in ((r as { spec: { range?: object } }).spec.range ?? {})))).toBe(true);
  });
});
