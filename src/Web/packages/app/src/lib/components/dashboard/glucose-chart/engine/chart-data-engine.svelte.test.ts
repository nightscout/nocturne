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
  it("reaches no further than a display-window consumer draws", async () => {
    // The sidebar sparkline and the clock faces render `displayDateRange` and
    // nothing wider, so readings outside it are points they would never draw.
    const sgvs = await glucoseOf({
      focusHours: 3,
      enablePredictions: false,
      dataWindow: "display",
    });

    expect(sgvs).toEqual([110, 120]);
  });

  it("keeps the full buffer by default", async () => {
    // Anything that renders `fullXDomain` — GlucoseChartCard's mini overview —
    // draws 48 hours whether or not it was handed them up front, so a consumer
    // that says nothing must keep the wide merge.
    const sgvs = await glucoseOf({ focusHours: 3, enablePredictions: false });

    expect(sgvs).toEqual([110, 120, 130, 140]);
  });

  it("includes a reading sitting exactly on either bound", async () => {
    // The bounds are the caller's own instants here, so both comparisons can be
    // pinned without depending on where the minute happens to have ticked.
    const from = Date.now() - 5 * HOUR;
    const to = Date.now() - 1 * HOUR;

    let engine!: ChartDataEngine;
    render(Harness, {
      props: {
        entries: [
          { _id: "before", type: "sgv", mills: from - 1, sgv: 60 },
          { _id: "on-from", type: "sgv", mills: from, sgv: 70 },
          { _id: "on-to", type: "sgv", mills: to, sgv: 80 },
          { _id: "after", type: "sgv", mills: to + 1, sgv: 90 },
        ],
        options: {
          enablePredictions: false,
          dateRange: { from: new Date(from), to: new Date(to) },
        },
        onengine: (e: ChartDataEngine) => (engine = e),
      },
    });
    await vi.waitFor(() => expect(engine.serverChartData).not.toBeNull());

    expect(engine.glucoseData.map((p) => p.sgv).sort((a, b) => a - b)).toEqual([
      70, 80,
    ]);
  });
});
