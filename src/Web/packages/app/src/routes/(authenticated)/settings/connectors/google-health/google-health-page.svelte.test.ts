import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { GoogleHealthSyncPhase, type GoogleHealthStatus } from "$lib/api";
import { googleHealthMocks } from "$lib/test-stubs/google-health";

import GoogleHealthPage from "./google-health-page.svelte";

vi.mock("$lib/api/generated/googleHealths.generated.remote", () => import("$lib/test-stubs/google-health"));
vi.mock("$api/generated/patientRecords.generated.remote", () => ({
  getPatientRecord: () => ({ current: null }),
}));

function status(overrides: Partial<GoogleHealthStatus> = {}): GoogleHealthStatus {
  return {
    configured: false,
    connected: false,
    clientId: "",
    callbackUrl: "",
    historyDays: 7,
    selectedTypes: ["steps", "heart-rate", "weight", "sleep"],
    grantedTypes: [],
    previewRequired: false,
    capabilities: [
      { dataType: "steps", displayName: "Steps", category: "Activity", supported: true, destination: "step-counts" },
      { dataType: "heart-rate", displayName: "Heart rate", category: "Vitals", supported: true, destination: "heart-rates" },
      { dataType: "weight", displayName: "Weight", category: "Body measurement", supported: true, destination: "body-weights" },
      { dataType: "body-fat", displayName: "Body fat", category: "Body measurement", supported: false },
    ],
    ...overrides,
  };
}

describe("Google Health connector page", () => {
  it.each([390, 1280])("renders and exports diagnostics at viewport width %s", async (width) => {
    const originalSize = [window.innerWidth, window.innerHeight];
    await page.viewport(width, 844);
    const createObjectURL = vi.spyOn(URL, "createObjectURL");
    try {
      googleHealthMocks.status.mockResolvedValue(status({
        configured: true, connected: true, isSyncing: true, errorCode: "internal_sync",
        syncPhase: GoogleHealthSyncPhase.Integrating, syncProgressPercent: 72,
        syncRun: {
          runId: "synthetic-run", outcome: "running", recordsWritten: 1200,
          events: [{ stage: "native_batch_completed", dataType: "steps", count: 500, durationMilliseconds: 125 }],
        },
      }));
      render(GoogleHealthPage);
      await expect.element(page.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "72");
      await page.getByText("Import diagnostics", { exact: true }).click();
      await expect.element(page.getByText("native_batch_completed")).toBeVisible();
      expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
      await page.getByRole("button", { name: "Download diagnostics" }).click();
      const payload = JSON.parse(await (createObjectURL.mock.calls[0][0] as Blob).text());
      expect(payload.runId).toBe("synthetic-run");
      expect(payload.events[0].durationMilliseconds).toBe(125);
      expect(payload.clientId).toBeUndefined();
    } finally {
      createObjectURL.mockRestore();
      await page.viewport(originalSize[0], originalSize[1]);
    }
  });

  it("keeps failed import diagnostics visible after syncing stops", async () => {
    googleHealthMocks.status.mockResolvedValue(status({
      configured: true, connected: true, isSyncing: false, errorCode: "internal_sync",
      syncRun: {
        runId: "test-run", outcome: "failed", recordsWritten: 12,
        events: [{ stage: "native_write", dataType: "sleep", sqlState: "57014",
          exceptionTypes: ["System.TimeoutException"], stackFrames: ["SleepSessionRepository.UpsertSessionAsync"] }],
      },
    }));
    render(GoogleHealthPage);
    await expect.element(page.getByText("Outcome: failed")).toBeVisible();
    await expect.element(page.getByText("SQLSTATE 57014")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Download diagnostics" })).toBeEnabled();
    await expect.element(page.getByRole("progressbar")).not.toBeInTheDocument();
  });

  it("polls progress and completion without a websocket event", async () => {
    googleHealthMocks.status.mockResolvedValue(status({
      configured: true, connected: true, isSyncing: true,
      syncPhase: GoogleHealthSyncPhase.Preparing,
    }));
    render(GoogleHealthPage);
    await expect.element(page.getByText("Preparing the import")).toBeVisible();
    googleHealthMocks.status.mockResolvedValue(status({
      configured: true, connected: true, isSyncing: true,
      syncPhase: GoogleHealthSyncPhase.Reading, syncDataType: "steps",
      syncProgressPercent: 22, syncPagesRead: 12,
    }));
    await expect.element(page.getByRole("progressbar", { name: "Google Health import progress" })).toHaveAttribute("aria-valuenow", "22");
    googleHealthMocks.status.mockResolvedValue(status({
      configured: true, connected: true, isSyncing: false,
    }));
    await expect.element(page.getByText("Google Health import completed.", { exact: true })).toBeVisible();
    await expect.element(page.getByRole("progressbar")).not.toBeInTheDocument();
  });

  beforeEach(() => {
    vi.resetAllMocks();
    googleHealthMocks.status.mockResolvedValue(status());
    googleHealthMocks.preview.mockResolvedValue({ items: [] });
  });

  it("shows detected, supported, and unsupported data types", async () => {
    googleHealthMocks.status.mockResolvedValue(status({ configured: true, connected: true }));
    googleHealthMocks.preview.mockResolvedValue({ items: [
      { dataType: "steps", granted: true, count: 42, supported: true },
      { dataType: "body-fat", granted: true, count: 3, supported: false },
    ] });
    render(GoogleHealthPage);
    await expect.element(page.getByRole("heading", { name: "Google Health" })).toBeVisible();
    await expect.element(page.getByLabelText("Import data from")).toBeVisible();
    await expect.element(page.getByText("Import enabled", { exact: true })).toBeVisible();
    await page.getByText("Body measurement", { exact: true }).click();
    await expect.element(page.getByText("Not yet supported by Nocturne")).toBeVisible();
    await expect.element(page.getByText("Step history")).toBeVisible();
  });

  it("allows problematic selected types to be unchecked and saved without syncing", async () => {
    googleHealthMocks.status.mockResolvedValue(status({ configured: true, connected: true, selectedTypes: ["steps", "heart-rate", "weight"] }));
    googleHealthMocks.preview.mockResolvedValue({ items: [
      { dataType: "steps", granted: true, supported: true, count: 4 },
      { dataType: "heart-rate", granted: true, supported: true, count: 0 },
      { dataType: "weight", granted: true, supported: true, count: 0, errorCode: "google_unavailable" },
    ] });
    render(GoogleHealthPage);
    await page.getByRole("checkbox", { name: "Import Heart rate" }).click();
    await page.getByText("Body measurement", { exact: true }).click();
    await page.getByRole("checkbox", { name: "Import Weight" }).click();
    await page.getByRole("button", { name: "Save import settings", exact: true }).click();
    expect(googleHealthMocks.save).toHaveBeenCalledWith(expect.objectContaining({ dataTypes: ["steps"] }));
    expect(googleHealthMocks.sync).not.toHaveBeenCalled();
    expect(googleHealthMocks.disconnect).not.toHaveBeenCalled();
  });

  it("saves an older history date and an empty selection without reconnecting", async () => {
    googleHealthMocks.status
      .mockResolvedValueOnce(status({ configured: true, connected: true, selectedTypes: ["heart-rate"], importFrom: new Date("2026-08-29T00:00:00Z") }))
      .mockResolvedValue(status({ configured: true, connected: true, selectedTypes: [], importFrom: new Date("2020-01-01T00:00:00Z") }));
    googleHealthMocks.preview.mockResolvedValue({ items: [
      { dataType: "heart-rate", granted: true, supported: true, count: 0 },
    ] });
    render(GoogleHealthPage);
    await page.getByRole("checkbox", { name: "Import Heart rate" }).click();
    await page.getByLabelText("Import data from").fill("2020-01-01");
    await page.getByRole("button", { name: "Save import settings", exact: true }).click();
    expect(googleHealthMocks.save).toHaveBeenCalledWith(expect.objectContaining({ dataTypes: [], importFrom: "2020-01-01T00:00:00.000Z", clientSecret: null }));
    expect(googleHealthMocks.sync).not.toHaveBeenCalled();
    expect(googleHealthMocks.start).not.toHaveBeenCalled();
    expect(googleHealthMocks.disconnect).not.toHaveBeenCalled();
    await expect.element(page.getByText("Google Health is connected. Imports are paused because no data types are selected.")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Sync now", exact: true })).toBeDisabled();
  });

  it("allows a supported empty type to be enabled for future measurements", async () => {
    googleHealthMocks.status.mockResolvedValue(status({ configured: true, connected: true, selectedTypes: ["steps"] }));
    googleHealthMocks.preview.mockResolvedValue({ items: [
      { dataType: "heart-rate", granted: true, supported: true, count: 0 },
    ] });
    render(GoogleHealthPage);
    await page.getByRole("checkbox", { name: "Import Heart rate" }).click();
    await expect.element(page.getByRole("checkbox", { name: "Import Heart rate" })).toBeChecked();
  });

  it("requires preview confirmation before claiming that data is importing", async () => {
    googleHealthMocks.status.mockResolvedValue(status({ configured: true, connected: true, previewRequired: true }));
    googleHealthMocks.preview.mockResolvedValue({ items: [
      { dataType: "steps", granted: true, count: 42, supported: true },
    ] });
    render(GoogleHealthPage);

    await expect.element(page.getByText("Review the available data below", { exact: false })).toBeVisible();
    await expect.element(page.getByText("Available to connect", { exact: true })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Sync now" })).toBeDisabled();
    await expect.element(page.getByRole("button", { name: "Save selection and import" })).toBeEnabled();
    expect(googleHealthMocks.sync).not.toHaveBeenCalled();
  });

  it("keeps import status tied to saved selection while checkboxes are edited", async () => {
    googleHealthMocks.status.mockResolvedValue(status({ configured: true, connected: true, selectedTypes: ["steps"] }));
    googleHealthMocks.preview.mockResolvedValue({ items: [
      { dataType: "steps", granted: true, count: 42, supported: true },
      { dataType: "weight", granted: true, count: 3, supported: true },
    ] });
    render(GoogleHealthPage);
    const steps = page.getByRole("row", { name: /Steps/ });
    const weight = page.getByRole("row", { name: /Weight/ });

    await expect.element(steps.getByText("Import enabled", { exact: true })).toBeVisible();
    await expect.element(weight.getByText("Available to connect", { exact: true })).toBeVisible();
    await page.getByRole("checkbox", { name: "Import Steps" }).click();
    await page.getByRole("checkbox", { name: "Import Weight" }).click();

    await expect.element(steps.getByText("Import enabled", { exact: true })).toBeVisible();
    await expect.element(weight.getByText("Available to connect", { exact: true })).toBeVisible();
    expect(googleHealthMocks.save).not.toHaveBeenCalled();
  });

  it("shows an import error for the affected saved data type", async () => {
    googleHealthMocks.status.mockResolvedValue(status({
      configured: true, connected: true, errorCode: "internal_sync_native_write", errorDataTypes: ["steps"],
    }));
    googleHealthMocks.preview.mockResolvedValue({ items: [
      { dataType: "steps", granted: true, count: 42, supported: true },
      { dataType: "weight", granted: true, count: 3, supported: true },
    ] });
    render(GoogleHealthPage);

    await expect.element(page.getByText("Import needs attention", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Import enabled", { exact: true })).toBeVisible();
  });

  it("shows server-side progress while a large import continues in the background", async () => {
    googleHealthMocks.status.mockResolvedValue(status({
      configured: true, connected: true, isSyncing: true, syncPhase: GoogleHealthSyncPhase.Reading, syncDataType: "steps",
      syncCompletedDataTypes: 1, syncTotalDataTypes: 4, syncPagesRead: 12, syncProgressPercent: 22,
    }));
    render(GoogleHealthPage);

    await expect.element(page.getByText("Import running in the background")).toBeVisible();
    await expect.element(page.getByText("Reading Steps")).toBeVisible();
    await expect.element(page.getByText("12 Google pages read for this data type")).toBeVisible();
    await expect.element(page.getByRole("progressbar", { name: "Google Health import progress" })).toHaveAttribute("aria-valuenow", "22");
    await expect.element(page.getByRole("button", { name: "Sync now" })).toBeDisabled();
    expect(googleHealthMocks.preview).not.toHaveBeenCalled();
  });
});
