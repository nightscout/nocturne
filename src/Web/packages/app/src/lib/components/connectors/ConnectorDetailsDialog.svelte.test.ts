import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import type { SyncResult } from "$lib/api/generated/nocturne-api-client";

let syncImpl: () => Promise<SyncResult>;

vi.mock("$lib/api", () => ({
  getApiClient: () => ({
    services: { triggerConnectorSync: () => syncImpl() },
  }),
}));

// The dialog uses Tooltip.Root, whose provider the app layout supplies; the wrapper stands in for it.
import ConnectorDetailsDialog from "./connector-details-dialog-test-wrapper.svelte";

async function syncReturning(result: SyncResult) {
  syncImpl = async () => result;

  render(ConnectorDetailsDialog, {
    props: {
      open: true,
      selectedConnector: {
        id: "nightscout",
        name: "Nightscout",
        status: "Active",
        state: "Configured",
        isHealthy: true,
      },
      selectedConnectorCapabilities: {
        supportsManualSync: true,
        supportsHistoricalSync: true,
      },
    },
  });

  await page.getByRole("button", { name: "Sync Now" }).click();
}

describe("ConnectorDetailsDialog", () => {
  it("reports the count a failed sync still landed", async () => {
    await syncReturning({
      success: false,
      message: "Failed to sync Notes: the source refused the request",
      errors: ["Failed to sync Notes: the source refused the request"],
      itemsSynced: { Glucose: 288, Boluses: 12 },
    });

    await expect
      .element(
        page.getByText("Failed to sync Notes: the source refused the request")
      )
      .toBeVisible();
    await expect.element(page.getByText("(300 items)")).toBeVisible();
  });

  it("reports a zero count for a failed sync that landed nothing", async () => {
    await syncReturning({
      success: false,
      message: "Sync failed while fetching data",
      errors: ["Failed to fetch Glucose"],
      itemsSynced: { Glucose: 0 },
    });

    await expect.element(page.getByText("(0 items)")).toBeVisible();
  });

  it("reports the count a successful sync landed", async () => {
    await syncReturning({
      success: true,
      message: "",
      errors: [],
      itemsSynced: { Glucose: 288 },
    });

    await expect.element(page.getByText("(288 items)")).toBeVisible();
  });
});
