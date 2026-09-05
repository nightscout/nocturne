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
  it("shows the chart and no empty state when data is already present", async () => {
    state.connectors = [];

    render(FirstReadingChartArea, {
      chart,
      bypass: true,
      recentHistoryReady: false,
      hasRecentHistory: false,
    });

    await expect.element(page.getByText("CHART SHOWN")).toBeVisible();
    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .not.toBeInTheDocument();
  });

  it("hides the chart behind the empty state for an instance that never had a reading", async () => {
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
      bypass: false,
      recentHistoryReady: true,
      hasRecentHistory: false,
    });

    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .toBeVisible();
    await expect.element(page.getByText("CHART SHOWN")).not.toBeVisible();
  });

  it("shows the chart for a dormant uploader-only instance that has recent history", async () => {
    state.connectors = [];

    render(FirstReadingChartArea, {
      chart,
      bypass: false,
      recentHistoryReady: true,
      hasRecentHistory: true,
    });

    await expect.element(page.getByText("CHART SHOWN")).toBeVisible();
    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .not.toBeInTheDocument();
  });
});
