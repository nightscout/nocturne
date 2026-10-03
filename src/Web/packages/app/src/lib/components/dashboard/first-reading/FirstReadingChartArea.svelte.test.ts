import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { createRawSnippet } from "svelte";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import type { ConnectorStatusDto } from "$lib/api/generated/nocturne-api-client";
import FirstReadingChartArea from "./FirstReadingChartArea.svelte";
import CoachHarness from "./FirstReadingCoachHarness.test.svelte";

const state = vi.hoisted(() => {
  const connectors: ConnectorStatusDto[] = [];
  return { connectors, reducedMotion: false };
});

vi.mock("@nocturne/watercolour", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@nocturne/watercolour")>()),
  prefersReducedMotion: () => state.reducedMotion,
}));

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

  it("does not mark the chart coach-eligible while the empty state is shown", async () => {
    state.connectors = [
      {
        id: "dexcom",
        name: "Dexcom Share",
        hasDatabaseConfig: true,
        totalEntries: 0,
      },
    ];

    render(CoachHarness, {
      bypass: false,
      recentHistoryReady: true,
      hasRecentHistory: false,
    });

    await expect
      .element(page.getByTestId("first-reading-empty-state"))
      .toBeVisible();
    await expect
      .element(page.getByTestId("coach-eligible"))
      .toHaveTextContent("no");
  });

  it("marks the chart coach-eligible when the chart is shown", async () => {
    state.connectors = [];

    render(CoachHarness, {
      bypass: true,
      recentHistoryReady: false,
      hasRecentHistory: false,
    });

    await expect
      .element(page.getByTestId("coach-eligible"))
      .toHaveTextContent("yes");
  });

  describe("first reading arrival", () => {
    const waiting: ConnectorStatusDto = {
      id: "dexcom",
      name: "Dexcom Share",
      hasDatabaseConfig: true,
      totalEntries: 0,
    };

    async function renderWaiting() {
      state.connectors = [waiting];
      const screen = render(FirstReadingChartArea, {
        chart,
        bypass: false,
        recentHistoryReady: true,
        hasRecentHistory: false,
      });
      await expect
        .element(page.getByTestId("first-reading-empty-state"))
        .toBeVisible();
      return screen;
    }

    it("shows the chart at once and fades the sunrise off it when the first reading arrives", async () => {
      state.reducedMotion = false;
      const screen = await renderWaiting();

      await screen.rerender({ bypass: true });

      await expect.element(page.getByText("CHART SHOWN")).toBeVisible();
      await expect
        .element(page.getByTestId("first-reading-arrival"))
        .toBeInTheDocument();
      await expect
        .element(page.getByTestId("first-reading-empty-state"))
        .not.toBeInTheDocument();
      await expect
        .element(page.getByTestId("first-reading-arrival"), { timeout: 3000 })
        .not.toBeInTheDocument();
    });

    it("swaps straight to the chart under reduced motion", async () => {
      state.reducedMotion = true;
      const screen = await renderWaiting();

      await screen.rerender({ bypass: true });

      await expect.element(page.getByText("CHART SHOWN")).toBeVisible();
      expect(
        document.querySelector('[data-testid="first-reading-arrival"]')
      ).toBeNull();
    });

    it("plays nothing when data arrives before the empty state was ever shown", async () => {
      state.reducedMotion = false;
      state.connectors = [];
      const screen = render(FirstReadingChartArea, {
        chart,
        bypass: false,
        recentHistoryReady: false,
        hasRecentHistory: false,
      });

      await screen.rerender({ bypass: true });

      await expect.element(page.getByText("CHART SHOWN")).toBeVisible();
      expect(
        document.querySelector('[data-testid="first-reading-arrival"]')
      ).toBeNull();
    });
  });
});
