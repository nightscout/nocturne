import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import type { SyncResult } from "#lib/api/generated/nocturne-api-client.js";

import ManualSyncDialog from "./ManualSyncDialog.svelte";

function renderDialog(manualSyncResults: Record<string, SyncResult> | null) {
  render(ManualSyncDialog, {
    props: {
      open: true,
      isManualSyncing: false,
      manualSyncResults,
      syncProgress: null,
    },
  });
}

describe("ManualSyncDialog", () => {
  it("shows each connector's server-measured duration", async () => {
    renderDialog({
      dexcom: { success: true, durationMs: 1250 },
      libre: { success: true, durationMs: 80 },
    });

    await expect.element(page.getByText("1250ms")).toBeVisible();
    await expect.element(page.getByText("80ms")).toBeVisible();
    await expect.element(page.getByText("2 of 2 connectors synced")).toBeVisible();
  });

  it("shows no duration for a connector the server did not time", async () => {
    renderDialog({ dexcom: { success: false, message: "Authentication failed" } });

    await expect.element(page.getByText("Authentication failed")).toBeVisible();
    await expect.element(page.getByText(/\dms$/)).not.toBeInTheDocument();
  });

  it("reports a partial outcome with the failed connector's message", async () => {
    renderDialog({
      dexcom: { success: true, durationMs: 900 },
      libre: { success: false, message: "Rate limited", durationMs: 40 },
    });

    await expect.element(page.getByText("Sync finished with 1 failed")).toBeVisible();
    await expect.element(page.getByText("Rate limited")).toBeVisible();
    await expect.element(page.getByText("40ms")).toBeVisible();
  });

  it("says there is nothing to sync when no connectors ran", async () => {
    renderDialog({});

    await expect.element(page.getByText("Nothing to sync")).toBeVisible();
  });
});
