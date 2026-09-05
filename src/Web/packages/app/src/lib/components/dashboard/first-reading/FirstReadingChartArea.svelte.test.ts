import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { createRawSnippet } from "svelte";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import type {
  ConnectorStatusDto,
  ServicesOverview,
} from "$lib/api/generated/nocturne-api-client";
import FirstReadingChartArea from "./FirstReadingChartArea.svelte";

const state = vi.hoisted(() => {
  const connectors: ConnectorStatusDto[] = [];
  const services: ServicesOverview = { activeDataSources: [] };
  return { connectors, services };
});

vi.mock("$api/generated/connectorStatus.generated.remote", () => ({
  getStatus: () => remoteQuery(() => state.connectors),
}));

vi.mock("$api/generated/services.generated.remote", () => ({
  getServicesOverview: () => remoteQuery(() => state.services),
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
    state.services = { activeDataSources: [] };

    render(FirstReadingChartArea, { chart });

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
    state.services = { activeDataSources: [] };

    render(FirstReadingChartArea, { chart });

    await expect.element(page.getByText("CHART SHOWN")).toBeVisible();
    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .not.toBeInTheDocument();
  });

  it("shows the chart when an uploader source has readings but no connector exists", async () => {
    state.connectors = [];
    state.services = {
      activeDataSources: [{ id: "xdrip", name: "xDrip", totalEntries: 12 }],
    };

    render(FirstReadingChartArea, { chart });

    await expect.element(page.getByText("CHART SHOWN")).toBeVisible();
    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .not.toBeInTheDocument();
  });
});
