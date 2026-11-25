import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, it, expect, vi } from "vitest";
import { createRawSnippet } from "svelte";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { ConnectorStatusDto } from "@nocturne/api-client";
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

    const heldAnimations: Animation[] = [];

    // The fade runs in real time, so a loaded runner can finish it between two awaits.
    function holdAnimations() {
      const animate = Element.prototype.animate;
      vi.spyOn(Element.prototype, "animate").mockImplementation(function (
        this: Element,
        ...args: Parameters<Element["animate"]>
      ) {
        const animation = animate.apply(this, args);
        animation.pause();
        heldAnimations.push(animation);
        return animation;
      });
    }

    afterEach(() => {
      vi.restoreAllMocks();
      heldAnimations.length = 0;
    });

    async function renderWaiting() {
      // The painting under the empty state comes from another chunk, so it says nothing about
      // whether the sunrise has loaded. A reading that lands first plays no sunrise.
      await import("#lib/watercolour/artwork.js");
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
      // The sunrise module was requested after the painting chunk, so once the painting is drawn
      // one more task lets the sunrise import settle too.
      await expect
        .poll(() => document.querySelector('[data-testid="first-reading-empty-state"] canvas'))
        .not.toBeNull();
      await new Promise((resolve) => setTimeout(resolve));
      return screen;
    }

    it("shows the chart at once and fades the sunrise off it when the first reading arrives", async () => {
      state.reducedMotion = false;
      holdAnimations();
      const screen = await renderWaiting();

      await screen.rerender({ bypass: true });

      await expect.element(page.getByText("CHART SHOWN")).toBeVisible();
      await expect
        .element(page.getByTestId("first-reading-arrival"))
        .toBeInTheDocument();
      await expect
        .element(page.getByTestId("first-reading-empty-state"))
        .not.toBeInTheDocument();

      expect(heldAnimations).not.toHaveLength(0);
      // The fade is a delay animation and then the fade proper, the second made when the first ends.
      await expect
        .poll(() => {
          for (const animation of heldAnimations.splice(0)) animation.finish();
          return document.querySelector('[data-testid="first-reading-arrival"]');
        })
        .toBeNull();
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
