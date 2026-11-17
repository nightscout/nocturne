import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, it, expect, vi } from "vitest";
import { createRawSnippet } from "svelte";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { ConnectorStatusDto } from "#lib/api/generated/nocturne-api-client.js";
import FirstReadingChartArea from "./FirstReadingChartArea.svelte";
import CoachHarness from "./FirstReadingCoachHarness.test.svelte";
import { releaseWatercolour, resetWatercolour } from "#lib/test-stubs/watercolour-deferral.svelte.js";

const state = vi.hoisted(() => {
  const connectors: ConnectorStatusDto[] = [];
  return { connectors, reducedMotion: false };
});

vi.mock("water-ui", async (importOriginal) => ({
  ...(await importOriginal<typeof import("water-ui")>()),
  prefersReducedMotion: () => state.reducedMotion,
}));

vi.mock("#lib/watercolour-deferral.svelte.js", () => import("#lib/test-stubs/watercolour-deferral.svelte.js"));

vi.mock("#lib/api/generated/connectorStatus.generated.remote.js", () => ({
  getStatus: () => remoteQuery(() => state.connectors),
}));

const chart = createRawSnippet(() => ({
  render: () => "<div>CHART SHOWN</div>",
}));

describe("FirstReadingChartArea", () => {
  beforeEach(() => {
    releaseWatercolour();
  });

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
      // The painting and the sunrise load together once deferred watercolour is due.
      await expect
        .poll(() => document.querySelector('[data-testid="first-reading-empty-state"] canvas'))
        .not.toBeNull();
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

    it("plays no sunrise for a reading that lands before deferred watercolour is due", async () => {
      state.reducedMotion = false;
      resetWatercolour();
      state.connectors = [waiting];
      const screen = render(FirstReadingChartArea, {
        chart,
        bypass: false,
        recentHistoryReady: true,
        hasRecentHistory: false,
      });
      await expect.element(page.getByTestId("first-reading-empty-state")).toBeVisible();

      await screen.rerender({ bypass: true });

      await expect.element(page.getByText("CHART SHOWN")).toBeVisible();
      expect(document.querySelector('[data-testid="first-reading-arrival"]')).toBeNull();
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
