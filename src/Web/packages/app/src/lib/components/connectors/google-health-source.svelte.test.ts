import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import type { GoogleHealthStatus } from "$lib/api";
import { goto } from "$lib/test-stubs/year-overview-runtime.svelte";
import GoogleHealthSourceRow from "./GoogleHealthSourceRow.svelte";
import ServerConnectorsCard from "./ServerConnectorsCard.svelte";

const connected: GoogleHealthStatus = {
  configured: true, connected: true, selectedTypes: ["steps", "sleep"],
  previewRequired: false, lastSync: new Date("2026-09-06T09:00:00Z"),
};

describe("Google Health source presentation", () => {
  beforeEach(() => vi.clearAllMocks());

  it("uses the same active styling as other connectors and opens its settings", async () => {
    render(ServerConnectorsCard, {
      googleHealth: connected,
      availableConnectors: [{ id: "dexcom", name: "Dexcom" }],
      connectorStatuses: [{ id: "dexcom", isEnabled: true, hasDatabaseConfig: true, isHealthy: true, state: "Active" }],
      connectorCapabilitiesById: {}, syncProgressByConnector: {}, activeDataSources: [],
      isLoadingConnectorStatuses: false, isManualSyncing: false, quickSyncingById: {},
      onRefreshStatuses: vi.fn(), onManualSync: vi.fn(), onQuickSync: vi.fn(), onConnectorClick: vi.fn(),
    });
    const google = page.getByRole("button", { name: /Google Health/ });
    const dexcom = page.getByRole("button", { name: /Dexcom/ });
    await expect.element(google).toHaveTextContent("Active");
    expect(google.element().className).toBe(dexcom.element().className);
    expect(google.element().className).toContain("border-green");
    await expect.element(google).toHaveTextContent("Last successful sync:");
    await expect.element(google).not.toHaveTextContent("0 records");
    await google.click();
    expect(goto).toHaveBeenCalledWith("/settings/connectors/google-health");
  });

  it.each([
    { change: { previewRequired: true }, label: "Configured" },
    { change: { selectedTypes: [] }, label: "Configured" },
    { change: { errorCode: "google_unavailable" }, label: "Error" },
    { change: { connected: false }, label: "Offline" },
  ])("does not show a healthy active status for $label", async ({ change, label }) => {
    render(GoogleHealthSourceRow, { connection: { ...connected, ...change } });
    const row = page.getByRole("button", { name: /Google Health/ });
    await expect.element(row).toHaveTextContent(label);
    await expect.element(row).not.toHaveTextContent("Active");
    expect(row.element().className).not.toContain("border-green");
  });

  it("does not invent an import count or successful sync before the first import", async () => {
    render(GoogleHealthSourceRow, { connection: { ...connected, lastSync: undefined } });
    await expect.element(page.getByText("Waiting for the first successful sync")).toBeVisible();
    await expect.element(page.getByRole("button", { name: /Google Health/ })).not.toHaveTextContent("0 records");
  });

  it.each([
    { change: { previewRequired: true }, guidance: "Review available data and confirm the import selection" },
    { change: { selectedTypes: [] }, guidance: "Choose at least one data type to start importing" },
  ])("explains the next action for $guidance", async ({ change, guidance }) => {
    render(GoogleHealthSourceRow, { connection: { ...connected, ...change } });
    await expect.element(page.getByRole("button", { name: /Google Health/ })).toHaveTextContent(guidance);
  });

  it("offers a working refresh when Google Health is the only configured connector", async () => {
    const refresh = vi.fn();
    render(ServerConnectorsCard, {
      googleHealth: connected, availableConnectors: [], connectorStatuses: [],
      connectorCapabilitiesById: {}, syncProgressByConnector: {}, activeDataSources: [],
      isLoadingConnectorStatuses: false, isManualSyncing: false, quickSyncingById: {},
      onRefreshStatuses: refresh, onManualSync: vi.fn(), onQuickSync: vi.fn(), onConnectorClick: vi.fn(),
    });
    await page.getByRole("button", { name: "Refresh", exact: true }).click();
    expect(refresh).toHaveBeenCalledOnce();
  });
});
