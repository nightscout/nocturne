import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { remoteQuery, remoteQueryError } from "$lib/test-stubs/remote-resource";
import type { ConnectorStatusDto } from "$lib/api/generated/nocturne-api-client";
import FirstReadingEmptyStateLoader from "./FirstReadingEmptyStateLoader.svelte";

const state = vi.hoisted(() => {
  const value: {
    mode: "value" | "error";
    connectors: ConnectorStatusDto[];
    error: unknown;
  } = { mode: "value", connectors: [], error: undefined };
  return value;
});

vi.mock("$api/generated/connectorStatus.generated.remote", () => ({
  getStatus: () =>
    state.mode === "error"
      ? remoteQueryError(state.error)
      : remoteQuery(() => state.connectors),
}));

describe("FirstReadingEmptyStateLoader", () => {
  it("shows the empty state when no reading has ever arrived", async () => {
    state.mode = "value";
    state.connectors = [
      {
        id: "dexcom",
        name: "Dexcom Share",
        hasDatabaseConfig: true,
        totalEntries: 0,
      },
    ];

    render(FirstReadingEmptyStateLoader, {
      recentHistoryReady: true,
      hasRecentHistory: false,
    });

    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .toBeVisible();
  });

  it("does not show the empty state when a connector has imported readings before", async () => {
    state.mode = "value";
    state.connectors = [
      {
        id: "dexcom",
        name: "Dexcom Share",
        hasDatabaseConfig: true,
        totalEntries: 288,
      },
    ];

    render(FirstReadingEmptyStateLoader, {
      recentHistoryReady: true,
      hasRecentHistory: false,
    });

    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .not.toBeInTheDocument();
  });

  it("does not show the empty state for a dormant uploader-only instance with recent history", async () => {
    state.mode = "value";
    state.connectors = [];

    render(FirstReadingEmptyStateLoader, {
      recentHistoryReady: true,
      hasRecentHistory: true,
    });

    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .not.toBeInTheDocument();
  });

  it("does not show the empty state while the recent-history load is in flight", async () => {
    state.mode = "value";
    state.connectors = [];

    render(FirstReadingEmptyStateLoader, {
      recentHistoryReady: false,
      hasRecentHistory: false,
    });

    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .not.toBeInTheDocument();
  });

  it("resolves and shows the empty state when the connector-status query fails", async () => {
    state.mode = "error";
    state.error = { status: 403, message: "Forbidden" };

    render(FirstReadingEmptyStateLoader, {
      recentHistoryReady: true,
      hasRecentHistory: false,
    });

    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .toBeVisible();
  });
});
