import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { CustomReport } from "#lib/api/generated/nocturne-api-client.js";
import { normalisedSpec, statTilesResult } from "../custom-report-fixtures.js";

const calls = vi.hoisted(() => ({
  runs: [] as unknown[],
  updates: [] as unknown[],
  deletes: [] as unknown[],
  gotos: [] as string[],
  copied: [] as string[],
}));
let saved: CustomReport;

vi.mock("#lib/api/generated/customReports.generated.remote.js", () => ({
  getCustomReport: () => remoteQuery(() => saved),
  runCustomReport: (arg: unknown) => {
    calls.runs.push(arg);
    return remoteQuery(() => ({ result: statTilesResult(), spec: normalisedSpec, errors: [] }));
  },
  updateCustomReport: remoteCommand((arg) => {
    calls.updates.push(arg);
    return saved;
  }),
  deleteCustomReport: remoteCommand((id) => {
    calls.deletes.push(id);
  }),
}));

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
    saved = { id: "a1", name: "Fortnight", spec: normalisedSpec, errors: [] };
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

  it("shows why a stored spec no longer runs, and does not run it", async () => {
    saved = {
      id: "a1",
      name: "Old",
      spec: { version: 0, panels: [] },
      errors: [{ path: "version", message: "0 is not a supported version. Send the number 1." }],
    };
    render(CustomReportPage, {});

    await expect.element(page.getByText("0 is not a supported version. Send the number 1.")).toBeVisible();
    await expect.element(page.getByText("Show the spec")).toBeVisible();
    expect(calls.runs).toEqual([]);
  });
});
