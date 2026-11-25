import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";

const store = vi.hoisted(() => ({
  currentBG: 0,
  currentEntry: null as { mills: number; sgv?: number } | null,
  bgDelta: null as number | null,
  direction: "",
  lastUpdated: Date.now(),
  now: Date.now(),
  timeSinceReading: "",
  connectionUnavailable: false,
  currentReadingLoaded: true,
  entries: [] as unknown[],
}));

vi.mock("#lib/stores/realtime-store.svelte.js", () => ({
  getRealtimeStore: () => store,
  tryGetRealtimeStore: () => store,
}));

vi.mock("#lib/api/generated/summaries.generated.remote.js", () => ({
  getSummary: () => ({ current: undefined }),
}));

// The sparkline is not under test: the value tile is.
vi.mock("#lib/components/dashboard/glucose-chart/engine/chart-data-engine.svelte.js", () => ({
  createChartDataEngine: () => ({}),
}));
vi.mock("#lib/components/dashboard/glucose-chart/GlucoseChartShell.svelte", async () => ({
  default: (await import("#lib/test-stubs/Empty.test-stub.svelte")).default,
}));

import SidebarGlucoseWidget from "./SidebarGlucoseWidget.svelte";

function reading(mgdl: number) {
  const mills = Date.now();
  store.currentEntry = { mills, sgv: mgdl };
  store.currentBG = mgdl;
  store.lastUpdated = mills;
  store.now = mills;
  store.entries = [store.currentEntry];
}

describe("SidebarGlucoseWidget", () => {
  beforeEach(() => {
    store.currentEntry = null;
    store.currentBG = 0;
    store.entries = [];
    store.currentReadingLoaded = true;
  });

  it("shows the current reading", async () => {
    reading(141);

    render(SidebarGlucoseWidget);

    await expect.element(page.getByText("141").first()).toBeInTheDocument();
  });

  it("shows no reading, not 0, when the newest reading has no value", async () => {
    reading(0);

    render(SidebarGlucoseWidget);

    await expect.element(page.getByText("No glucose reading").first()).toBeInTheDocument();
    expect(page.getByText("0", { exact: true }).elements()).toHaveLength(0);
  });

  it("shows no reading, not 0, when there are no readings", async () => {
    store.entries = [{ type: "mbg", mills: Date.now(), mbg: 120 }];

    render(SidebarGlucoseWidget);

    await expect.element(page.getByText("No glucose reading").first()).toBeInTheDocument();
    expect(page.getByText("0", { exact: true }).elements()).toHaveLength(0);
  });
});
