import { render } from "vitest-browser-svelte";
import { afterEach, beforeEach, describe, it, expect, vi } from "vitest";

vi.mock("$api/chart-data.remote", () => ({
  getChartData: vi.fn(() => {
    const result = Promise.resolve(served) as Promise<typeof served> & {
      run: () => Promise<typeof served>;
    };
    result.run = () => Promise.resolve(served);
    return result;
  }),
}));
// The store registers its handlers on the client; capturing them lets a test deliver a
// socket event the way the bridge would.
const socketHandlers = vi.hoisted(() => ({}) as Record<string, (event: unknown) => void>);
vi.mock("$lib/websocket/websocket-client.svelte", () => ({
  WebSocketClient: class {
    on(event: string, handler: (event: unknown) => void) {
      socketHandlers[event] = handler;
    }
    connect() {}
    ensureConnected() {}
    disconnect() {}
  },
}));
vi.mock("$api/predictions.remote", () => ({
  getPredictions: vi.fn(async () => null),
  getPredictionStatus: vi.fn(async () => ({ available: false })),
}));

import { error } from "@sveltejs/kit";
import { flushSync } from "svelte";
import type { RealtimeStore } from "$lib/stores/realtime-store.svelte";
import { getChartData } from "$api/chart-data.remote";
import { getPredictionStatus } from "$api/predictions.remote";
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

describe("chart data engine — realtime glucose source", () => {
  const sidebar: ChartDataEngineOptions = {
    focusHours: 3,
    enablePredictions: false,
    dataWindow: "display",
    glucoseSource: "realtime",
  };

  function mountSidebar(
    options: ChartDataEngineOptions = sidebar,
    onstore?: (store: RealtimeStore) => void
  ) {
    let engine!: ChartDataEngine;
    vi.mocked(getChartData).mockClear();
    render(Harness, {
      props: {
        entries,
        options,
        onengine: (e: ChartDataEngine) => (engine = e),
        onstore,
      },
    });
    return engine;
  }

  it("draws the display window from the store's readings without asking the server", async () => {
    const engine = mountSidebar();
    await new Promise((r) => setTimeout(r, 50));

    expect(getChartData).not.toHaveBeenCalled();
    expect(engine.serverChartData).toBeNull();
    expect(engine.glucoseData.map((p) => p.sgv).sort((a, b) => a - b)).toEqual([110, 120]);
  });

  it("uses the fixed band with no target until the store has the therapy state's thresholds", () => {
    const engine = mountSidebar();

    expect(engine.thresholds).toMatchObject({
      veryLow: 54,
      low: 70,
      high: 180,
      veryHigh: 250,
      targetLow: null,
      targetHigh: null,
    });
  });

  it("takes the thresholds the store holds, colouring points by them", () => {
    const engine = mountSidebar(sidebar, (s) => {
      s.chartThresholds = {
        veryLow: 54,
        low: 70,
        high: 115,
        veryHigh: 250,
        targetLow: 90,
        targetHigh: 110,
      };
    });

    expect(engine.thresholds).toMatchObject({ high: 115, targetLow: 90, targetHigh: 110 });
    const colourOf = (sgv: number) => engine.glucoseData.find((p) => p.sgv === sgv)?.color;
    expect(colourOf(110)).toBe("var(--glucose-in-range)");
    expect(colourOf(120)).toBe("var(--glucose-high)");
  });

  it("takes the axis ceiling from the store's thresholds", () => {
    const engine = mountSidebar(sidebar, (s) => {
      s.chartThresholds = { low: 70, high: 180, veryLow: 54, veryHigh: 250, glucoseYMax: 400 };
    });

    expect(engine.glucoseYMax).toBe(400);
  });

  it("makes no remote call at all", async () => {
    vi.mocked(getPredictionStatus).mockClear();
    mountSidebar();
    await new Promise((r) => setTimeout(r, 50));

    expect(getChartData).not.toHaveBeenCalled();
    expect(getPredictionStatus).not.toHaveBeenCalled();
  });

  it("checks prediction status when predictions are enabled", async () => {
    vi.mocked(getPredictionStatus).mockClear();
    mountSidebar({ focusHours: 3 });

    await vi.waitFor(() => expect(getPredictionStatus).toHaveBeenCalledTimes(1));
  });

  it("does not refresh recent data when treatment data changes or the fallback timer fires", async () => {
    vi.useFakeTimers();
    try {
      let store!: RealtimeStore;
      mountSidebar({ ...sidebar, initialChartData: transformChartData({}) }, (s) => {
        store = s;
        s.isReady = true;
      });
      await vi.advanceTimersByTimeAsync(0);
      store.boluses = [{ id: "new" }];
      await vi.advanceTimersByTimeAsync(6 * MINUTE);

      expect(getChartData).not.toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
  });

  it("still asks the server by default", async () => {
    mountSidebar({ focusHours: 3, enablePredictions: false });

    await vi.waitFor(() => expect(getChartData).toHaveBeenCalledTimes(1));
  });
});

describe("chart data engine — refused fetch", () => {
  it("surfaces the API's reason for refusing the range", async () => {
    const detail = "Date range must not exceed 366 days.";
    // What SvelteKit hands a client for a query whose server half threw
    // error(400, detail): an HttpError, not an Error.
    let refused: unknown;
    try {
      error(400, detail);
    } catch (e) {
      refused = e;
    }
    vi.mocked(getChartData).mockRejectedValueOnce(refused);

    let engine!: ChartDataEngine;
    render(Harness, {
      props: {
        entries: [],
        options: { focusHours: 3, enablePredictions: false },
        onengine: (e: ChartDataEngine) => (engine = e),
      },
    });

    await vi.waitFor(() => expect(engine.chartDataError).toBe(detail));
    expect(engine.serverChartData).toBeNull();
  });

  it("shows its own message for a share-host 401 rather than the status phrase", async () => {
    // The generated query's share-host 401 arm: error(401, 'Unauthorized').
    let refused: unknown;
    try {
      error(401, "Unauthorized");
    } catch (e) {
      refused = e;
    }
    vi.mocked(getChartData).mockRejectedValueOnce(refused);

    let engine!: ChartDataEngine;
    render(Harness, {
      props: {
        entries: [],
        options: { focusHours: 3, enablePredictions: false },
        onengine: (e: ChartDataEngine) => (engine = e),
      },
    });

    await vi.waitFor(() =>
      expect(engine.chartDataError).toBe("The chart data could not be loaded.")
    );
  });
});

describe("chart data engine — SSR data refresh", () => {
  const SECOND = 1000;
  type Served = typeof served;
  type Store = RealtimeStore;

  function resolving(value: Served) {
    const result = Promise.resolve(value) as Promise<Served> & { run: () => Promise<Served> };
    result.run = () => result;
    return result;
  }

  function boluslike(minutesAgo: number): Served {
    const data = transformChartData({});
    data.bolusMarkers = [
      { time: new Date(Date.now() - minutesAgo * MINUTE), insulin: 2 },
    ] as never;
    return data;
  }

  async function mount(
    initial: Served = transformChartData({}),
    initialLoad?: (s: Store) => void,
    options: ChartDataEngineOptions = {
      focusHours: 3,
      enablePredictions: false,
      initialChartData: initial,
    }
  ) {
    vi.useFakeTimers();
    vi.mocked(getChartData).mockReset();
    vi.mocked(getChartData).mockImplementation((() => resolving(boluslike(1))) as never);
    let engine!: ChartDataEngine;
    let store!: Store;
    render(Harness, {
      props: {
        entries: [],
        options,
        onengine: (e: ChartDataEngine) => (engine = e),
        // The store is a process-wide singleton, so reset what earlier tests left on it
        // before the engine is created.
        onstore: (s: Store) => {
          store = s;
          s.isReady = false;
          s.boluses = [];
          s.carbIntakes = [];
          s.bgChecks = [];
          s.deviceEvents = [];
          s.deviceStatuses = [];
        },
      },
    });
    await vi.advanceTimersByTimeAsync(0);
    initialLoad?.(store);
    store.isReady = true;
    flushSync();
    await vi.advanceTimersByTimeAsync(0);
    return { engine, store };
  }

  const calls = () => vi.mocked(getChartData).mock.calls.length;

  afterEach(() => {
    vi.useRealTimers();
  });

  it("does not fetch on mount, including when the store's initial load lands", async () => {
    await mount(undefined, (s) => {
      s.boluses = [{ id: "loaded" }];
    });
    await vi.advanceTimersByTimeAsync(4 * SECOND);

    expect(calls()).toBe(0);
  });

  it("refetches once the debounce passes after the store's treatment data changes, replacing the window", async () => {
    const initial = transformChartData({});
    initial.bolusMarkers = [
      { time: new Date(Date.now() - 2 * MINUTE), insulin: 5 },
    ] as never;
    const { engine, store } = await mount(initial);

    store.boluses = [{ id: "new" }];
    await vi.advanceTimersByTimeAsync(1 * SECOND);
    expect(calls()).toBe(0);
    await vi.advanceTimersByTimeAsync(2 * SECOND);

    expect(calls()).toBe(1);
    expect(engine.serverChartData?.bolusMarkers.map((m) => m.insulin)).toEqual([2]);
  });

  it("coalesces changes inside the debounce into one fetch", async () => {
    const { store } = await mount();

    store.boluses = [{ id: "a" }];
    await vi.advanceTimersByTimeAsync(1 * SECOND);
    store.boluses = [{ id: "a" }, { id: "b" }];
    await vi.advanceTimersByTimeAsync(4 * SECOND);

    expect(calls()).toBe(1);
  });

  it("refreshes on the 5-minute fallback, not on each minute, and re-arms after a data refresh", async () => {
    const { store } = await mount();

    // The harness never starts the store's clock, so tick it by hand as production does.
    for (let minute = 0; minute < 4; minute++) {
      await vi.advanceTimersByTimeAsync(MINUTE);
      store.now = Date.now();
      flushSync();
      expect(calls()).toBe(0);
    }
    await vi.advanceTimersByTimeAsync(MINUTE + SECOND);
    expect(calls()).toBe(1);

    store.boluses = [{ id: "a" }];
    await vi.advanceTimersByTimeAsync(3 * SECOND);
    expect(calls()).toBe(2);
    // The data refresh restarted the timer: 4 minutes later is still not due.
    await vi.advanceTimersByTimeAsync(4 * MINUTE);
    expect(calls()).toBe(2);
    await vi.advanceTimersByTimeAsync(1 * MINUTE);
    expect(calls()).toBe(3);
  });

  it("drops a response that a later refresh has overtaken", async () => {
    const { engine, store } = await mount();
    let releaseSlow!: (v: Served) => void;
    const slow = new Promise<Served>((r) => (releaseSlow = r)) as Promise<Served> & {
      run: () => Promise<Served>;
    };
    slow.run = () => slow;
    vi.mocked(getChartData).mockImplementationOnce((() => slow) as never);

    store.boluses = [{ id: "a" }];
    await vi.advanceTimersByTimeAsync(3 * SECOND);
    store.boluses = [{ id: "a" }, { id: "b" }];
    await vi.advanceTimersByTimeAsync(3 * SECOND);
    expect(engine.serverChartData?.bolusMarkers.map((m) => m.insulin)).toEqual([2]);

    releaseSlow(transformChartData({}));
    await vi.advanceTimersByTimeAsync(0);

    expect(engine.serverChartData?.bolusMarkers).toHaveLength(1);
  });

  const pageData = (insulin: number) => {
    const data = transformChartData({});
    data.bolusMarkers = [{ time: new Date(Date.now() - MINUTE), insulin }] as never;
    return data;
  };
  const reactiveOptions = (initialChartData: Served) => {
    const options = $state<ChartDataEngineOptions>({
      focusHours: 3,
      enablePredictions: false,
      initialChartData,
    });
    return options;
  };

  it("adopts replaced page data as a fresh window", async () => {
    const options = reactiveOptions(transformChartData({}));
    const { engine } = await mount(undefined, undefined, options);

    // Every successful form submit runs invalidateAll(), which hands the page new data.
    options.initialChartData = pageData(7);
    flushSync();
    await vi.advanceTimersByTimeAsync(0);

    expect(engine.serverChartData?.bolusMarkers.map((m) => m.insulin)).toEqual([7]);
    expect(calls()).toBe(0);
  });

  it("adopts page data from the window the server loaded, not from the client clock", async () => {
    const insulinAt = (start: number, offsetMs: number, insulin: number) =>
      ({ time: new Date(start + offsetMs), insulin }) as never;
    const loadStart = Date.now() - 6 * HOUR;
    const initial = transformChartData({});
    initial.bolusMarkers = [
      insulinAt(loadStart, -HOUR, 1),
      insulinAt(loadStart, 2 * MINUTE, 2),
    ];
    const options = reactiveOptions(initial);
    options.initialWindowStart = loadStart;
    const { engine } = await mount(undefined, undefined, options);

    // The reload was computed against a window starting 5 minutes earlier than
    // the one the first load covered, so it holds the same marker again.
    const next = transformChartData({});
    next.bolusMarkers = [insulinAt(loadStart, -3 * MINUTE, 9), insulinAt(loadStart, 2 * MINUTE, 2)];
    options.initialWindowStart = loadStart - 5 * MINUTE;
    options.initialChartData = next;
    flushSync();
    await vi.advanceTimersByTimeAsync(0);

    expect(engine.serverChartData?.bolusMarkers.map((m) => m.insulin)).toEqual([1, 9, 2]);
  });

  it("still refreshes when page data is replaced inside the debounce of a write", async () => {
    const options = reactiveOptions(transformChartData({}));
    const { engine, store } = await mount(undefined, undefined, options);

    store.noteTreatmentWrite();
    await vi.advanceTimersByTimeAsync(1 * SECOND);
    options.initialChartData = pageData(7);
    flushSync();
    await vi.advanceTimersByTimeAsync(3 * SECOND);

    // The timer the write armed survives the replacement and its result is the latest.
    expect(calls()).toBe(1);
    expect(engine.serverChartData?.bolusMarkers.map((m) => m.insulin)).toEqual([2]);
  });

  it("does not let an older in-flight refresh overwrite newer page data", async () => {
    const options = reactiveOptions(transformChartData({}));
    const { engine, store } = await mount(undefined, undefined, options);
    let releaseSlow!: (v: Served) => void;
    const slow = new Promise<Served>((r) => (releaseSlow = r)) as Promise<Served> & {
      run: () => Promise<Served>;
    };
    slow.run = () => slow;
    vi.mocked(getChartData).mockImplementationOnce((() => slow) as never);
    store.noteTreatmentWrite();
    await vi.advanceTimersByTimeAsync(3 * SECOND);

    options.initialChartData = pageData(7);
    flushSync();
    releaseSlow(pageData(99));
    await vi.advanceTimersByTimeAsync(0);

    expect(engine.serverChartData?.bolusMarkers.map((m) => m.insulin)).toEqual([7]);
  });

  it("refreshes when the app itself writes a treatment", async () => {
    const { store } = await mount();

    store.noteTreatmentWrite();
    await vi.advanceTimersByTimeAsync(3 * SECOND);

    expect(calls()).toBe(1);
  });

  it("refreshes when a legacy treatments socket event is created, edited or deleted", async () => {
    await mount();
    const deliver = (kind: "create" | "update" | "delete") =>
      socketHandlers[kind]({ colName: "treatments", doc: { _id: "t1" } });

    deliver("update");
    await vi.advanceTimersByTimeAsync(3 * SECOND);
    expect(calls()).toBe(1);
    deliver("delete");
    await vi.advanceTimersByTimeAsync(3 * SECOND);
    expect(calls()).toBe(2);
    deliver("create");
    await vi.advanceTimersByTimeAsync(3 * SECOND);
    expect(calls()).toBe(3);
  });

  describe("in a hidden tab", () => {
    let visibility = "visible";
    const setVisibility = (value: "visible" | "hidden") => {
      visibility = value;
      document.dispatchEvent(new Event("visibilitychange"));
    };

    beforeEach(() => {
      visibility = "visible";
      Object.defineProperty(document, "visibilityState", {
        configurable: true,
        get: () => visibility,
      });
    });
    afterEach(() => {
      Reflect.deleteProperty(document, "visibilityState");
    });

    it("skips the fallback fetch and refreshes once the tab is visible again", async () => {
      await mount();
      setVisibility("hidden");

      await vi.advanceTimersByTimeAsync(11 * MINUTE);
      expect(calls()).toBe(0);

      setVisibility("visible");
      await vi.advanceTimersByTimeAsync(3 * SECOND);
      expect(calls()).toBe(1);
    });

    it("does not refresh on becoming visible when nothing was skipped", async () => {
      await mount();
      setVisibility("hidden");
      setVisibility("visible");
      await vi.advanceTimersByTimeAsync(3 * SECOND);

      expect(calls()).toBe(0);
    });
  });
});

describe("chart data engine — fetch range", () => {
  it("does not refetch when the clock moves within the same five-minute bucket", async () => {
    let store!: { now: number };
    const bucket = Math.floor(Date.now() / (5 * MINUTE)) * 5 * MINUTE;
    vi.mocked(getChartData).mockClear();
    render(Harness, {
      props: {
        entries: [],
        options: { enablePredictions: false, focusHours: 3 },
        onengine: () => {},
        onstore: (s: { now: number }) => {
          store = s;
          s.now = bucket + 1 * MINUTE;
        },
      },
    });
    await vi.waitFor(() => expect(getChartData).toHaveBeenCalledTimes(1));

    store.now = bucket + 2 * MINUTE;
    await new Promise((r) => setTimeout(r, 50));
    expect(getChartData).toHaveBeenCalledTimes(1);

    store.now = bucket + 6 * MINUTE;
    await vi.waitFor(() => expect(getChartData).toHaveBeenCalledTimes(2));
  });
});
