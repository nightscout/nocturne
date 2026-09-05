import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { createRawSnippet } from "svelte";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import type { ConnectorStatusDto } from "$lib/api/generated/nocturne-api-client";
import FirstReadingChartArea from "./FirstReadingChartArea.svelte";

const state = vi.hoisted(() => {
  const connectors: ConnectorStatusDto[] = [];
  return { connectors };
});

vi.mock("$api/generated/connectorStatus.generated.remote", () => ({
  getStatus: () => remoteQuery(() => state.connectors),
}));

const chart = createRawSnippet(() => ({
  render: () => "<div>CHART SHOWN</div>",
}));

describe("FirstReadingChartArea", () => {
  it("shows the empty state when no reading has ever arrived", async () => {
    state.connectors = [
      {
        id: "dexcom",
        name: "Dexcom Share",
        hasDatabaseConfig: true,
        totalEntries: 0,
      },
    ];

    render(FirstReadingChartArea, {
      chart,
      recentHistoryReady: true,
      hasRecentHistory: false,
    });

    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .toBeVisible();
    await expect.element(page.getByText("CHART SHOWN")).not.toBeVisible();
  });

  it("shows the chart when a connector has imported readings before", async () => {
    state.connectors = [
      {
        id: "dexcom",
        name: "Dexcom Share",
        hasDatabaseConfig: true,
        totalEntries: 288,
      },
    ];

    render(FirstReadingChartArea, {
      chart,
      recentHistoryReady: true,
      hasRecentHistory: false,
    });

    await expect.element(page.getByText("CHART SHOWN")).toBeVisible();
    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .not.toBeInTheDocument();
  });

  it("shows the chart for a dormant uploader-only instance that has recent history but no connector", async () => {
    state.connectors = [];

    render(FirstReadingChartArea, {
      chart,
      recentHistoryReady: true,
      hasRecentHistory: true,
    });

    await expect.element(page.getByText("CHART SHOWN")).toBeVisible();
    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .not.toBeInTheDocument();
  });

  it("does not render the empty state while the recent-history load is in flight", async () => {
    state.connectors = [];

    render(FirstReadingChartArea, {
      chart,
      recentHistoryReady: false,
      hasRecentHistory: false,
    });

    await expect.element(page.getByText("CHART SHOWN")).toBeVisible();
    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .not.toBeInTheDocument();
  });
});
