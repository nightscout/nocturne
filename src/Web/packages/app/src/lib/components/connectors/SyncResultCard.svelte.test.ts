import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import SyncResultCard from "./SyncResultCard.svelte";
import type { SyncResult } from "$lib/api/generated/nocturne-api-client";

function makeResult(overrides: Partial<SyncResult> = {}): SyncResult {
  return {
    success: true,
    message: "",
    itemsSynced: {},
    errors: [],
    ...overrides,
  };
}

describe("SyncResultCard", () => {
  it("reports the counts a failed sync still landed", async () => {
    render(SyncResultCard, {
      syncResult: makeResult({
        success: false,
        message: "Failed to sync Notes: the source refused the request",
        errors: ["Failed to sync Notes: the source refused the request"],
        itemsSynced: { Glucose: 288, Boluses: 12 },
      }),
      displayName: "Nightscout",
    });

    await expect.element(page.getByText("Sync failed")).toBeVisible();
    await expect.element(page.getByText("288 glucose")).toBeVisible();
    await expect.element(page.getByText("12 boluses")).toBeVisible();
  });

  it("badges a type the failed sync checked and found empty", async () => {
    render(SyncResultCard, {
      syncResult: makeResult({
        success: false,
        message: "Failed to sync Notes: the source refused the request",
        errors: ["Failed to sync Notes: the source refused the request"],
        itemsSynced: { Glucose: 0 },
      }),
      displayName: "Nightscout",
    });

    await expect.element(page.getByText("0 glucose")).toBeVisible();
  });

  it("headlines the reason the sync reported rather than its own fallback", async () => {
    render(SyncResultCard, {
      syncResult: makeResult({
        success: false,
        message: "Sync failed while fetching data",
        errors: ["Chunk 2/5 failed (2026-01-08 to 2026-01-15)"],
      }),
      displayName: "Nightscout",
    });

    await expect
      .element(page.getByText("Sync failed while fetching data"))
      .toBeVisible();
    await expect
      .element(page.getByText("An error occurred during sync."))
      .not.toBeInTheDocument();
  });

  it("falls back to its own copy when the sync reported no message", async () => {
    render(SyncResultCard, {
      syncResult: makeResult({ success: false, errors: ["Sync error"] }),
      displayName: "Nightscout",
    });

    await expect
      .element(page.getByText("An error occurred during sync."))
      .toBeVisible();
  });

  it("reports the counts a successful sync landed", async () => {
    render(SyncResultCard, {
      syncResult: makeResult({
        success: true,
        itemsSynced: { Glucose: 288 },
      }),
      displayName: "Nightscout",
    });

    await expect.element(page.getByText("Sync completed")).toBeVisible();
    await expect.element(page.getByText("288 glucose")).toBeVisible();
  });
});
