import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { CustomReport } from "#lib/api/generated/nocturne-api-client.js";
import { catalogue, statisticsTemplate } from "#lib/components/reports/custom/builder/schema-form.fixtures.js";
import { statTilesResult } from "../../custom-report-fixtures.js";

const calls = vi.hoisted(() => ({ runs: [] as unknown[], updates: [] as unknown[], creates: [] as unknown[], gotos: [] as string[] }));
let saved: CustomReport;

vi.mock("#lib/api/report-image.remote.js", () => ({ drawReportPanelImage: () => Promise.resolve("") }));

vi.mock("#lib/api/generated/customReports.generated.remote.js", () => ({
  getCustomReport: () => remoteQuery(() => saved),
  getCustomReportCatalogue: () => remoteQuery(() => catalogue),
  getCustomReports: () => remoteQuery(() => []),
  runCustomReport: (arg: { spec: unknown }) => {
    calls.runs.push(arg);
    return remoteQuery(() => ({ result: statTilesResult(), spec: arg.spec, errors: [] }));
  },
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
