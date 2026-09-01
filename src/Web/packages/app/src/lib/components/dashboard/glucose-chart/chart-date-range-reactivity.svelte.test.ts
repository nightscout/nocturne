import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi } from "vitest";

type ChartWindow = { startTime: number; endTime: number; intervalMinutes: number };

// `vi.mock` factories are hoisted above every other statement, so the spy has to
// be created in a hoisted block for both the factory and the assertions to see it.
const { getChartData } = vi.hoisted(() => ({
  getChartData: vi.fn((window: ChartWindow) => {
    void window;
    return Promise.resolve(transformed());
  }),
}));

vi.mock("$api/chart-data.remote", () => ({ getChartData }));
vi.mock("$api/predictions.remote", () => ({
  getPredictions: vi.fn(async () => null),
  getPredictionStatus: vi.fn(async () => ({ available: false })),
}));

import { transformChartData } from "$lib/utils/chart-data-transform";
import Harness from "./GlucoseChartRangeHarness.test.svelte";

// The server half of the merge is deliberately empty: what is under test is which
// window the chart asks for, not what comes back.
function transformed() {
  return transformChartData({});
}

const DAY = 24 * 60 * 60 * 1000;

function day(offsetDays: number): { from: Date; to: Date } {
  const from = new Date(Date.UTC(2026, 7, 29) + offsetDays * DAY);
  return { from, to: new Date(from.getTime() + DAY - 1) };
}

/** The window starts the chart has asked the server for. */
function requestedStarts(): number[] {
  return getChartData.mock.calls.map(([window]) => window.startTime);
}

/**
 * Stepping a day on Day in Review swaps this prop and nothing else. Each component
 * builds its OWN engine, and an engine built from a flattened `{ dateRange }` object
 * literal reads the prop once — so the chart keeps drawing whichever day it was
 * mounted on. Both are asserted because fixing one leaves the other frozen, and Day
 * in Review — the surface this was reported on — renders the Card.
 */
describe.each([
  ["GlucoseChart", "chart"],
  ["GlucoseChartCard", "card"],
] as const)("%s date range", (_name, component) => {
  it("refetches when the range prop changes", async () => {
    getChartData.mockClear();

    let setRange!: (range: { from: Date; to: Date }) => void;
    render(Harness, {
      props: { component, initialRange: day(0), onready: (set) => (setRange = set) },
    });

    await vi.waitFor(() =>
      expect(requestedStarts()).toContain(day(0).from.getTime())
    );

    setRange(day(-1));

    // Asked for at all, rather than asked for last: the sibling case's chart is
    // still mounted and can land a fetch of its own in between.
    await vi.waitFor(() =>
      expect(requestedStarts()).toContain(day(-1).from.getTime())
    );
  });
});
