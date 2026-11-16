import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { CustomReport } from "#lib/api/generated/nocturne-api-client.js";
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

vi.mock("#lib/api/generated/customReports.generated.remote.js", () => ({
  getCustomReport: () => remoteQuery(() => saved),
  getCustomReportCatalogue: () => remoteQuery(() => liveCatalogue),
  runCustomReport: remoteCommand((arg) => {
    calls.runs.push(arg);
    return { result: runResult(), spec: normalisedSpec, errors: [] };
  }),
  updateCustomReport: remoteCommand((arg) => {
    calls.updates.push(arg);
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

describe("saved custom report page", () => {
  beforeEach(() => {
    for (const list of Object.values(calls)) list.length = 0;
    pageState.params = { id: "a1" };
    pageState.url = new URL("http://localhost/reports/custom/a1") as typeof pageState.url;
    saved = { id: "a1", name: "Fortnight", spec: normalisedSpec, errors: [], canEdit: true };
    runResult = statTilesResult;
    image.refuse = null;
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
    render(CustomReportPage, {});
    await vi.waitFor(() => expect(calls.runs).toHaveLength(1));

    await page.getByRole("radio", { name: "7 days" }).click();

    await vi.waitFor(() => expect(calls.runs).toHaveLength(2));
    expect(calls.runs[1]).toEqual({ spec: { ...normalisedSpec, range: { hours: 168 }, panels: [{ ...normalisedSpec.panels[0], range: { hours: 168 } }] } });
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
    await vi.waitFor(() => expect(calls.updates).toEqual([{ id: "a1", request: { name: "Two weeks of lunches" } }]));

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
});
