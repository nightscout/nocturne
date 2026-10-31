import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { CustomReport } from "#lib/api/generated/nocturne-api-client.js";
import { chartResult, normalisedSpec, statTilesResult } from "../custom-report-fixtures.js";

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

  it("runs the saved spec and draws it under the report's name", async () => {
    render(CustomReportPage, {});

    await expect.element(page.getByRole("heading", { name: "Fortnight" })).toBeVisible();
    await expect.element(page.getByText("Readings: 4025.")).toBeVisible();
    expect(calls.runs).toEqual([{ spec: normalisedSpec }]);
  });

  it("renames, copies the link and deletes after the reader confirms", async () => {
    render(CustomReportPage, {});

    await page.getByRole("button", { name: "Rename" }).click();
    await page.getByLabelText("Name").fill("Two weeks of lunches");
    await page.getByRole("button", { name: "Save" }).click();
    await vi.waitFor(() => expect(calls.updates).toEqual([{ id: "a1", request: { name: "Two weeks of lunches" } }]));

    await page.getByRole("button", { name: "Copy link" }).click();
    await vi.waitFor(() => expect(calls.copied).toEqual(["http://localhost/reports/custom/a1"]));

    await page.getByRole("button", { name: "Delete" }).click();
    expect(calls.deletes).toEqual([]);
    await page.getByRole("alertdialog").getByRole("button", { name: "Delete" }).click();
    await vi.waitFor(() => expect(calls.deletes).toEqual(["a1"]));
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom"]));
  });

  it("offers no Rename or Delete when the API says the reader cannot change the report", async () => {
    saved = { ...saved, canEdit: false };
    render(CustomReportPage, {});

    await expect.element(page.getByRole("button", { name: "Copy link" })).toBeVisible();
    expect(page.getByRole("button", { name: "Rename" }).elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Delete" }).elements()).toHaveLength(0);
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
    await expect.element(page.getByText("Show the spec")).toBeVisible();
    expect(calls.runs).toEqual([]);
  });
});
