import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { goto } from "$app/navigation";
import type { GoogleHealthStatus, ServicesOverview } from "$lib/api";

import GoogleHealthSourceRow from "./GoogleHealthSourceRow.svelte";
import ServerConnectorsCard from "./ServerConnectorsCard.svelte";
import ConnectorsPage from "$routes/(authenticated)/settings/connectors/+page.svelte";

const overviewMocks = vi.hoisted(() => ({ services: vi.fn(), google: vi.fn() }));
vi.mock("$api/generated/services.generated.remote", () => ({
  getServicesOverview: overviewMocks.services,
  getConnectorCapabilities: () => ({ current: null }),
  triggerConnectorSync: vi.fn(),
}));
vi.mock("$api/generated/googleHealths.generated.remote", () => ({ getGoogleHealth: overviewMocks.google }));
vi.mock("$api/generated/connectorStatus.generated.remote", () => ({ getStatus: () => ({ current: [] }) }));
vi.mock("$lib/stores/realtime-store.svelte", () => ({ getRealtimeStore: () => ({ syncProgressByConnector: {} }) }));
vi.mock("@nocturne/coach", () => ({ coachmark: () => () => {} }));
vi.mock("$lib/components/settings/ConnectedApps.svelte", () => ({ default: () => {} }));
vi.mock("$lib/components/settings/ClientDevices.svelte", () => ({ default: () => {} }));
vi.mock("$lib/components/settings/ApiTokens.svelte", () => ({ default: () => {} }));
vi.mock("./UploaderSetupDialog.svelte", () => ({ default: () => {} }));
vi.mock("./ConnectorDetailsDialog.svelte", () => ({ default: () => {} }));
vi.mock("./ManualSyncDialog.svelte", () => ({ default: () => {} }));
vi.mock("./DataSourceManageDialog.svelte", () => ({ default: () => {} }));

vi.mock("$app/navigation", () => ({ goto: vi.fn() }));

const connected: GoogleHealthStatus = {
  configured: true, connected: true, selectedTypes: ["steps", "sleep"],
  previewRequired: false, lastSync: new Date("2026-09-06T09:00:00Z"),
};

describe("Google Health source presentation", () => {
  beforeEach(() => vi.clearAllMocks());

  it("uses the same active styling as other connectors and opens its settings", async () => {
    render(ServerConnectorsCard, {
      googleHealth: connected,
      availableConnectors: [{ id: "dexcom", name: "Dexcom" }, { id: "googlehealth", name: "Google Health" }],
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
    { change: { previewRequired: true }, label: "Configured", guidance: "Review available data and confirm the import selection" },
    { change: { selectedTypes: [] }, label: "Configured", guidance: "Choose at least one data type to start importing" },
    { change: { errorCode: "google_unavailable" }, label: "Error", guidance: null },
    { change: { connected: false }, label: "Offline", guidance: null },
  ])("does not show a healthy active status for $label", async ({ change, label, guidance }) => {
    render(GoogleHealthSourceRow, { connection: { ...connected, ...change } });
    const row = page.getByRole("button", { name: /Google Health/ });
    await expect.element(row).toHaveTextContent(label);
    await expect.element(row).not.toHaveTextContent("Active");
    expect(row.element().className).not.toContain("border-green");
    if (guidance) await expect.element(row).toHaveTextContent(guidance);
  });

  it("does not invent an import count or successful sync before the first import", async () => {
    render(GoogleHealthSourceRow, { connection: { ...connected, lastSync: undefined } });
    await expect.element(page.getByText("Waiting for the first successful sync")).toBeVisible();
    await expect.element(page.getByRole("button", { name: /Google Health/ })).not.toHaveTextContent("0 records");
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

  it.each(["sourceType", "deviceId"])("keeps the disconnected source visible without duplicating its generic %s row", async (identifier) => {
    const overview: ServicesOverview = {
      activeDataSources: [{ id: "google", name: "Generic Google source", [identifier]: "google-health-connector" }],
      availableConnectors: [{ id: "googlehealth", name: "Google Health" }],
    };
    overviewMocks.services.mockReturnValue({ current: overview });
    overviewMocks.google.mockReturnValue({ current: { ...connected, connected: false } });
    render(ConnectorsPage);

    await expect.element(page.getByText("Reconnect to resume importing").first()).toBeVisible();
    expect(page.getByRole("button", { name: /Google Health/ }).elements()).toHaveLength(2);
    await expect.element(page.getByText("Generic Google source", { exact: true })).not.toBeInTheDocument();
    await expect.element(page.getByText("No data sources detected")).not.toBeInTheDocument();
    await page.getByRole("button", { name: /Google Health/ }).first().click();
    expect(goto).toHaveBeenCalledWith("/settings/connectors/google-health");
  });

  it.each([false, true])("shows the empty state when no configured or visible sources remain (stale source: %s)", async (staleSource) => {
    overviewMocks.services.mockReturnValue({ current: {
      activeDataSources: staleSource ? [{ id: "google", sourceType: "google-health-connector" }] : undefined,
    } });
    overviewMocks.google.mockReturnValue({ current: { configured: false, connected: false } });
    render(ConnectorsPage);

    await expect.element(page.getByText("No data sources detected")).toBeVisible();
    await expect.element(page.getByText("Reconnect to resume importing")).not.toBeInTheDocument();
  });
});
