import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import type { GoogleHealthStatus } from "$lib/api";
import { googleHealthMocks } from "$lib/test-stubs/google-health";
import GoogleHealthPage from "./google-health-page.svelte";

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
      { dataType: "steps", supported: true, destination: "step-counts" },
      { dataType: "body-fat", supported: false },
    ],
    ...overrides,
  };
}

describe("Google Health connector page", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    googleHealthMocks.status.mockResolvedValue(status());
    googleHealthMocks.preview.mockResolvedValue({ items: [] });
  });

  it("uses the standard connector presentation and an explicit history date", async () => {
    render(GoogleHealthPage);
    await expect.element(page.getByRole("heading", { name: "Google Health" })).toBeVisible();
    await expect.element(page.getByText("Server connector for health and fitness data")).toBeVisible();
    await expect.element(page.getByLabelText("Import data from")).toBeVisible();
  });

  it("shows the effective legacy history window when no explicit date is saved", async () => {
    const expected = new Date(Date.now() - 7 * 86_400_000)
      .toISOString()
      .slice(0, 10);
    render(GoogleHealthPage);

    await expect.element(page.getByLabelText("Import data from")).toHaveValue(expected);
  });

  it("shows detected, supported, and unsupported data types", async () => {
    googleHealthMocks.status.mockResolvedValue(status({ configured: true, connected: true }));
    googleHealthMocks.preview.mockResolvedValue({ items: [
      { dataType: "steps", granted: true, count: 42, supported: true },
      { dataType: "body-fat", granted: true, count: 3, supported: false },
    ] });
    render(GoogleHealthPage);
    await expect.element(page.getByText("Import enabled", { exact: true })).toBeVisible();
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

  it("explains the history safety limit without silently truncating the import", async () => {
    googleHealthMocks.status.mockResolvedValue(status({ configured: true, connected: true }));
    googleHealthMocks.preview.mockRejectedValue({ status: 400, body: { message: "history_too_large" } });
    render(GoogleHealthPage);

    await expect.element(page.getByRole("alert")).toHaveTextContent("Google exceeded the pagination safety limit");
    await expect.element(page.getByRole("alert")).toHaveTextContent("readings/history_too_large");
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
      configured: true, connected: true, isSyncing: true, syncPhase: "reading", syncDataType: "steps",
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
