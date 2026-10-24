import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { page } from "$app/state";
import ServerConnectorsCard from "./ServerConnectorsCard.svelte";

function renderCard(connectorStatuses: object[] = []) {
  render(ServerConnectorsCard, {
    props: {
      availableConnectors: [
        { id: "dexcom", name: "Dexcom", description: "Glucose from Dexcom Share" },
      ],
      connectorStatuses,
      connectorCapabilitiesById: {},
      syncProgressByConnector: {},
      activeDataSources: [],
      isLoadingConnectorStatuses: false,
      isManualSyncing: false,
      quickSyncingById: {},
      onRefreshStatuses: vi.fn(),
      onManualSync: vi.fn(),
      onQuickSync: vi.fn(),
      onConnectorClick: vi.fn(),
      googleHealth: null,
      googleHealthLoading: false,
      googleHealthFailed: false,
    },
  });
}

describe("ServerConnectorsCard", () => {
  beforeEach(() => {
    page.data.effectivePermissions = ["tenant.settings"];
    page.data.refusedAsDemoSubject = false;
  });

  it("marks a connector with no configuration and no data as not configured", async () => {
    renderCard();

    await expect.element(browserPage.getByRole("link", { name: "Dexcom" })).toBeVisible();
    await expect.element(browserPage.getByText("Not configured")).toBeVisible();
    await expect.element(browserPage.getByText("Glucose from Dexcom Share")).toBeVisible();
  });
});
