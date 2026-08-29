import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi } from "vitest";

vi.mock("$api/chart-data.remote", () => ({
  getChartData: vi.fn(async () => served),
}));
vi.mock("$api/predictions.remote", () => ({
  getPredictions: vi.fn(async () => null),
  getPredictionStatus: vi.fn(async () => ({ available: false })),
}));

import { transformChartData } from "$lib/utils/chart-data-transform";
import type { Entry } from "$lib/websocket/types";
import Harness from "./chart-data-engine-harness.test.svelte";
import type {
  ChartDataEngine,
  ChartDataEngineOptions,
} from "./chart-data-engine.svelte";

// The server half of the merge is deliberately empty, so every point the engine
// produces came from the realtime store and the window under test is the only
// thing deciding which ones survive.
const served = transformChartData({});

const MINUTE = 60 * 1000;
const HOUR = 60 * MINUTE;

function reading(minutesAgo: number, sgv: number): Entry {
  return {
    _id: `e-${minutesAgo}`,
    type: "sgv",
    mills: Date.now() - minutesAgo * MINUTE,
    sgv,
  };
}

/**
 * The realtime store holds the last 1000 readings — several days' worth — so
 * what bounds the merge decides how much of that a chart is handed.
 */
const entries = [
  reading(10, 110),
  reading(120, 120),
  reading(6 * 60, 130),
  reading(30 * 60, 140),
];

async function glucoseOf(options: ChartDataEngineOptions) {
  let engine!: ChartDataEngine;
  render(Harness, {
    props: {
      entries,
      options,
      onengine: (e: ChartDataEngine) => (engine = e),
    },
  });

  // The engine fetches its server half in an effect; nothing merges until it lands.
  await vi.waitFor(() => expect(engine.serverChartData).not.toBeNull());
  return engine.glucoseData.map((p) => p.sgv).sort((a, b) => a - b);
}

describe("chart data engine — realtime merge window", () => {
  it("gives a self-fetching consumer only the window it displays", async () => {
    // The sidebar widget and the clock face: no range, no preloaded data, so the
    // engine fetched three hours and must not merge readings from outside it.
    const sgvs = await glucoseOf({ focusHours: 3, enablePredictions: false });

    expect(sgvs).toEqual([110, 120]);
  });

  it("keeps the full buffer for a consumer that preloaded one", async () => {
    // The dashboard hands the engine 48 hours via SSR, and its MiniOverview draws
    // all of it, so the merge has to span the same 48 hours.
    const sgvs = await glucoseOf({
      focusHours: 3,
      enablePredictions: false,
      initialChartData: served,
    });

    expect(sgvs).toEqual([110, 120, 130, 140]);
  });

  it("keeps an explicitly requested range", async () => {
    const sgvs = await glucoseOf({
      focusHours: 3,
      enablePredictions: false,
      dateRange: {
        from: new Date(Date.now() - 40 * HOUR),
        to: new Date(Date.now() + HOUR),
      },
    });

    expect(sgvs).toEqual([110, 120, 130, 140]);
  });
});
