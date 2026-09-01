import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi } from "vitest";

type ChartWindow = {
  startTime: number;
  endTime: number;
  intervalMinutes: number;
};

// `vi.mock` factories are hoisted above every other statement, so the spy has to
// be created in a hoisted block for both the factory and the assertions to see it.
const { getChartData } = vi.hoisted(() => ({
  getChartData: vi.fn((_window: ChartWindow) => Promise.resolve(transformed())),
}));

vi.mock("$api/chart-data.remote", () => ({ getChartData }));
vi.mock("$api/predictions.remote", () => ({
  getPredictions: vi.fn(async () => null),
  getPredictionStatus: vi.fn(async () => ({ available: false })),
}));

import { transformChartData } from "$lib/utils/chart-data-transform";
import Harness from "./GlucoseChartRangeHarness.test.svelte";

// The payload is irrelevant: the assertion is on the request, not the response.
function transformed() {
  return transformChartData({});
}

const DAY = 24 * 60 * 60 * 1000;

function day(offsetDays: number): { from: Date; to: Date } {
  const from = new Date(Date.UTC(2026, 7, 29) + offsetDays * DAY);
  return { from, to: new Date(from.getTime() + DAY - 1) };
}

function requestedStarts(): number[] {
  return getChartData.mock.calls.map(([window]) => window.startTime);
}

/**
 * Each component builds its own engine, so each has to keep its `dateRange`
 * prop reactive independently — see `ChartDataEngineOptions.dateRange`.
 */
describe.each([
  ["GlucoseChart", "chart"],
  ["GlucoseChartCard", "card"],
] as const)("%s date range", (_name, component) => {
  it("refetches when the range prop changes", async () => {
    getChartData.mockClear();

    let setRange!: (range: { from: Date; to: Date }) => void;
    render(Harness, {
      props: {
        component,
        initialRange: day(0),
        onready: (set) => (setRange = set),
      },
    });

    await vi.waitFor(() =>
      expect(requestedStarts().at(-1)).toBe(day(0).from.getTime())
    );

    setRange(day(-1));

    await vi.waitFor(() =>
      expect(requestedStarts().at(-1)).toBe(day(-1).from.getTime())
    );
  });
});
