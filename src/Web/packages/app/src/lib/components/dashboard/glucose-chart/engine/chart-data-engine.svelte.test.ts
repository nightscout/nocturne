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

async function engineFor(options: ChartDataEngineOptions) {
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
  return engine;
}

async function glucoseOf(options: ChartDataEngineOptions) {
  const engine = await engineFor(options);
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

  it("keeps the full buffer for a consumer that preloaded one", async () => {
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

describe("chart data engine — reference stability", () => {
  // Svelte re-executes a derived it has flagged dirty on every read while more
  // than one batch is alive, so a merge that allocated per read republished a new
  // array each time and re-dirtied every chart scale reading it — the loop in
  // #995. Repeated reads with unchanged inputs must hand back one instance.
  it("hands out the same series until its inputs change", async () => {
    const engine = await engineFor({
      focusHours: 3,
      enablePredictions: false,
      dataWindow: "display",
    });

    const first = engine.glucoseData;
    expect(engine.glucoseData).toBe(first);
    expect(engine.glucoseData).toBe(first);
  });
});
