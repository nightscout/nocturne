import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import {
  OuraImportPhase,
  OuraImportState,
  type OuraImportStatus,
} from "#lib/api/generated/nocturne-api-client.js";

const notFound = { status: 404, body: { message: "Not found" } };

let latest: () => Promise<OuraImportStatus>;
let upload: () => Promise<OuraImportStatus>;

vi.mock("#lib/api/generated/ouras.generated.remote.js", async () => {
  const { createQueryResource, remoteForm } = await import("#lib/test-stubs/remote-resource.js");
  return {
    getOuraImport: () => createQueryResource(() => latest()),
    importOuraExport: remoteForm("unchecked", () => upload()),
  };
});

const refreshed = vi.hoisted(() => ({ summary: [] as string[], statuses: 0 }));

vi.mock("#lib/api/generated/services.generated.remote.js", () => ({
  getConnectorDataSummary: (id: string) => ({
    refresh: () => {
      refreshed.summary.push(id);
      return Promise.resolve();
    },
  }),
}));

vi.mock("#lib/api/generated/configurations.generated.remote.js", () => ({
  getAllConnectorStatus: () => ({
    refresh: () => {
      refreshed.statuses++;
      return Promise.resolve();
    },
  }),
}));

import OuraImportCard from "./OuraImportCard.svelte";

const props = { connectorId: "oura" };

const running: OuraImportStatus = {
  jobId: "job-1",
  state: OuraImportState.Running,
  phase: OuraImportPhase.HeartRate,
  sleepSessions: 730,
  heartRates: 12000,
  stepDays: 0,
};

async function chooseAndImport() {
  const zip = new File(["PK"], "oura.zip", { type: "application/zip" });
  await userEvent.upload(page.getByLabelText(/Data export/), zip);
  await page.getByRole("button", { name: "Import" }).click();
}

describe("the Oura import card", () => {
  beforeEach(() => {
    refreshed.summary = [];
    refreshed.statuses = 0;
    latest = () => Promise.reject(notFound);
    upload = () => Promise.resolve(running);
  });

  it("shows nothing about an import when there is none", async () => {
    render(OuraImportCard, props);

    await expect.element(page.getByRole("button", { name: "Import" })).toBeVisible();
    await expect.element(page.getByTestId("oura-import-progress")).not.toBeInTheDocument();
    await expect.element(page.getByTestId("oura-import-stopped")).not.toBeInTheDocument();
  });

  it("follows an upload's import from its progress to its final counts", async () => {
    let uploaded = false;
    let polls = 0;
    upload = () => {
      uploaded = true;
      return Promise.resolve(running);
    };
    latest = () =>
      !uploaded
        ? Promise.reject(notFound)
        : Promise.resolve(
            ++polls < 2
              ? running
              : { ...running, state: OuraImportState.Completed, phase: undefined, heartRates: 210240, stepDays: 730 }
          );
    render(OuraImportCard, props);

    await chooseAndImport();

    const progress = page.getByTestId("oura-import-progress");
    await expect.element(progress).toHaveTextContent(/Importing heart rate readings/);
    await expect.element(progress).toHaveTextContent(/Heart rate readings: 12,000/);
    await expect.element(page.getByRole("button", { name: "Import" })).toBeDisabled();
    await expect
      .element(page.getByTestId("oura-import-result"), { timeout: 8000 })
      .toHaveTextContent(/Heart rate readings: 210,240\. Days of steps: 730/);
    expect(refreshed.summary).toEqual(["oura"]);
    expect(refreshed.statuses).toBe(1);
  });

  it("refreshes nothing for an import that ended before the page opened", async () => {
    latest = () => Promise.resolve({ ...running, state: OuraImportState.Completed, phase: undefined });

    render(OuraImportCard, props);

    await expect.element(page.getByTestId("oura-import-result")).toBeVisible();
    expect(refreshed.summary).toEqual([]);
  });

  it("says a delete is running when the upload is refused for one, and follows no import", async () => {
    upload = () => Promise.reject({ status: 409, body: { message: "delete_running" } });
    render(OuraImportCard, props);

    await chooseAndImport();

    await expect.element(page.getByText(/Nocturne is deleting the Oura data/)).toBeVisible();
    await expect.element(page.getByTestId("oura-import-progress")).not.toBeInTheDocument();
  });

  it("finds an import that is already running when the page opens", async () => {
    latest = () => Promise.resolve(running);

    render(OuraImportCard, props);

    await expect.element(page.getByTestId("oura-import-progress")).toBeVisible();
  });

  it("says what a partial import wrote, and that importing the same export again completes it", async () => {
    latest = () =>
      Promise.resolve({ ...running, state: OuraImportState.Partial, phase: undefined, errorCode: "import_failed" });

    render(OuraImportCard, props);

    const partial = page.getByTestId("oura-import-partial");
    await expect.element(partial).toHaveTextContent(/Sleep sessions: 730\. Heart rate readings: 12,000/);
    await expect.element(partial).toHaveTextContent(/import the same export again/);
  });

  it("says the import stopped when the server no longer has the import it followed", async () => {
    let polls = 0;
    latest = () => (++polls < 2 ? Promise.resolve(running) : Promise.reject(notFound));

    render(OuraImportCard, props);

    await expect.element(page.getByTestId("oura-import-progress")).toBeVisible();
    const stopped = page.getByTestId("oura-import-stopped");
    await expect.element(stopped, { timeout: 8000 }).toHaveTextContent(/import the same export again/);
    await expect.element(page.getByTestId("oura-import-progress")).not.toBeInTheDocument();
  });

  it("says an import is in progress when a second one is refused", async () => {
    upload = () => Promise.reject({ status: 409, body: { message: "import_running" } });
    render(OuraImportCard, props);

    await chooseAndImport();

    await expect.element(page.getByText("An import is in progress. Wait until it is complete.")).toBeVisible();
  });
});
