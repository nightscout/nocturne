import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

const api = vi.hoisted(() => ({
  getCurrentTherapyState: vi.fn(),
  apsGetAll: vi.fn(),
  emptyPage: vi.fn(),
}));

vi.mock("$lib/api/client", () => ({
  getApiClient: () => ({
    currentTherapyState: { getCurrentTherapyState: api.getCurrentTherapyState },
    apsSnapshot: { getAll: api.apsGetAll },
    sensorGlucose: { getAll: api.emptyPage },
    bolus: { getAll: api.emptyPage },
    nutrition: { getCarbIntakes: api.emptyPage },
    bGCheck: { getAll: api.emptyPage },
    note: { getAll: api.emptyPage },
    deviceEvent: { getAll: api.emptyPage },
  }),
}));

vi.mock("svelte-sonner", () => ({
  toast: Object.assign(vi.fn(), {
    success: vi.fn(),
    error: vi.fn(),
    warning: vi.fn(),
    info: vi.fn(),
  }),
}));

import { toast } from "svelte-sonner";
import {
  RECENT_READINGS,
  RealtimeStore,
  loadInitialGlucose,
  sensorGlucoseToEntry,
} from "./realtime-store.svelte";
import type {
  ConnectionInfo,
  StorageEvent,
  SyncProgressEvent,
  TrackerUpdateEvent,
  WebSocketConnectionStatus,
} from "$lib/websocket/types";
import type { SensorGlucose, TrackerInstanceDto } from "$lib/api";

/** The realtime/backfill entry points, which the class keeps private. */
interface StoreInternals {
  handleCreate(event: StorageEvent): void;
  handleUpdate(event: StorageEvent): void;
  handleDelete(event: StorageEvent): void;
  performBackfillIfNeeded(force?: boolean): Promise<void>;
  handleDataUpdate(event: { data: unknown[] }): void;
  handleVisibilityChange: (() => void) | null;
  websocketClient: {
    connectionStatus: WebSocketConnectionStatus;
    ensureConnected(): void;
    eventHandlers: {
      connect?: (info: ConnectionInfo) => void;
      disconnect?: (reason: string) => void;
      connect_error?: (error: Error) => void;
      syncProgress?: (event: SyncProgressEvent) => void;
      trackerUpdate?: (event: TrackerUpdateEvent) => void;
    };
  };
}

type TestStore = StoreInternals &
  Pick<
    RealtimeStore,
    | "connectionUnavailable"
    | "connectionPresentation"
    | "currentReservoir"
    | "entries"
    | "boluses"
    | "currentEntry"
    | "bgDelta"
    | "direction"
    | "syncProgressByConnector"
    | "trackerInstances"
    | "destroy"
    | "initialize"
  >;

/** Store instance with an empty socket URL, so nothing connects. */
function makeStore(): TestStore {
  const store = new RealtimeStore({
    url: "",
    reconnectAttempts: 0,
    reconnectDelay: 0,
    maxReconnectDelay: 0,
    pingTimeout: 0,
    pingInterval: 0,
  });
  return store as unknown as TestStore;
}

function deviceStatus(id: string): StorageEvent {
  return {
    colName: "devicestatus",
    doc: { _id: id, mills: Date.now(), pump: {} },
  };
}

describe("RealtimeStore connection presentation", () => {
  const info: ConnectionInfo = { clientId: "socket", serverTime: "", version: "" };

  beforeEach(() => {
    vi.useFakeTimers();
    api.getCurrentTherapyState.mockResolvedValue({ reservoir: null });
    api.apsGetAll.mockResolvedValue({ data: [] });
    api.emptyPage.mockResolvedValue({ data: [] });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  /** The socket events as the client emits them: status first, then the handler. */
  function connected(store: TestStore): void {
    store.websocketClient.connectionStatus = "connected";
    store.websocketClient.eventHandlers.connect?.(info);
  }

  function dropped(store: TestStore): void {
    store.websocketClient.connectionStatus = "disconnected";
    store.websocketClient.eventHandlers.disconnect?.("transport close");
  }

  /** An initialized store on a page whose visibility the test flips, driving the
   *  store's own `visibilitychange` listener. */
  async function onPage(): Promise<{ store: TestStore; show(): void; hide(): void }> {
    const page = {
      visibilityState: "visible",
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    };
    vi.stubGlobal("document", page);
    vi.stubGlobal("window", { addEventListener: vi.fn(), removeEventListener: vi.fn() });
    const store = makeStore();
    await store.initialize();
    const flip = (state: "visible" | "hidden") => {
      page.visibilityState = state;
      store.handleVisibilityChange?.();
    };
    return { store, show: () => flip("visible"), hide: () => flip("hidden") };
  }

  it("waits before presenting a foreground disconnect and clears it on reconnect", async () => {
    const store = makeStore();
    connected(store);
    dropped(store);

    await vi.advanceTimersByTimeAsync(9_999);
    expect(store.connectionUnavailable).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    expect(store.connectionUnavailable).toBe(true);

    connected(store);
    expect(store.connectionUnavailable).toBe(false);
    store.destroy();
  });

  it("does not present a transient connection error", async () => {
    const store = makeStore();
    store.websocketClient.connectionStatus = "error";
    store.websocketClient.eventHandlers.connect_error?.(new Error("temporary"));
    await vi.advanceTimersByTimeAsync(5_000);
    connected(store);
    await vi.advanceTimersByTimeAsync(10_000);

    expect(store.connectionUnavailable).toBe(false);
    store.destroy();
  });

  it("presents a first connect that keeps failing", async () => {
    const store = makeStore();
    store.websocketClient.connectionStatus = "error";
    store.websocketClient.eventHandlers.connect_error?.(new Error("network down"));
    await vi.advanceTimersByTimeAsync(10_000);

    expect(store.connectionUnavailable).toBe(true);
    expect(toast.warning).toHaveBeenCalledTimes(1);
    store.destroy();
  });

  it("announces the recovery of a first connect it reported as unavailable", async () => {
    const store = makeStore();
    store.websocketClient.connectionStatus = "error";
    store.websocketClient.eventHandlers.connect_error?.(new Error("network down"));
    await vi.advanceTimersByTimeAsync(10_000);

    connected(store);

    expect(toast.success).toHaveBeenCalledTimes(1);
    expect(store.connectionPresentation).toBe("live");
    store.destroy();
  });

  it("shows a reported outage that ends in a denial as not live, not failed", async () => {
    const store = makeStore();
    connected(store);
    dropped(store);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(store.connectionPresentation).toBe("unavailable");

    store.websocketClient.connectionStatus = "unauthorized";

    expect(store.connectionUnavailable).toBe(false);
    expect(store.connectionPresentation).toBe("denied");
    store.destroy();
  });

  it("does not present a disconnect while the page is hidden", async () => {
    const page = {
      visibilityState: "hidden",
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    };
    vi.stubGlobal("document", page);
    const store = makeStore();
    dropped(store);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(store.connectionUnavailable).toBe(false);

    page.visibilityState = "visible";
    dropped(store);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(store.connectionUnavailable).toBe(true);
    store.destroy();
  });

  it("resumes a socket that dropped in a background tab without presenting an error", async () => {
    const { store, show, hide } = await onPage();
    const ensureConnected = vi.spyOn(store.websocketClient, "ensureConnected");
    connected(store);

    hide();
    dropped(store);
    await vi.advanceTimersByTimeAsync(20 * 60_000);
    show();

    expect(ensureConnected).toHaveBeenCalledTimes(1);
    expect(store.connectionUnavailable).toBe(false);
    expect(store.connectionPresentation).toBe("pending");
    await vi.advanceTimersByTimeAsync(5_000);
    connected(store);
    await vi.advanceTimersByTimeAsync(10_000);

    expect(store.connectionUnavailable).toBe(false);
    expect(toast.warning).not.toHaveBeenCalled();
    store.destroy();
  });

  describe("background polling", () => {
    let initial = 0;
    const rangeQueries = () => api.emptyPage.mock.calls.length - initial;
    const hiddenTab = async () => {
      const page = await onPage();
      connected(page.store);
      await vi.advanceTimersByTimeAsync(0);
      initial = api.emptyPage.mock.calls.length;
      page.hide();
      return page;
    };

    it("does not poll while a live socket keeps delivering", async () => {
      const { store } = await hiddenTab();

      for (let minute = 0; minute < 20; minute += 4) {
        store.handleCreate({ colName: "entries", doc: { _id: `e${minute}`, type: "sgv", sgv: 100, mills: Date.now() } });
        await vi.advanceTimersByTimeAsync(4 * 60_000);
      }

      expect(rangeQueries()).toBe(0);
      store.destroy();
    });

    it("polls a connected but silent socket once the data is stale", async () => {
      const { store } = await hiddenTab();

      await vi.advanceTimersByTimeAsync(5 * 60_000);
      expect(rangeQueries()).toBe(0);
      await vi.advanceTimersByTimeAsync(60_000);

      expect(rangeQueries()).toBeGreaterThan(0);
      store.destroy();
    });

    it("polls while the socket is down", async () => {
      const { store } = await hiddenTab();
      dropped(store);

      await vi.advanceTimersByTimeAsync(30_000);

      expect(rangeQueries()).toBeGreaterThan(0);
      store.destroy();
    });
  });

  it("presents a resumed tab whose socket stays down", async () => {
    const { store, show, hide } = await onPage();
    connected(store);
    hide();
    dropped(store);
    show();

    await vi.advanceTimersByTimeAsync(10_000);

    expect(store.connectionUnavailable).toBe(true);
    store.destroy();
  });

  it("keeps a reported outage through tab switches, announcing it once", async () => {
    const { store, show, hide } = await onPage();
    connected(store);
    dropped(store);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(store.connectionUnavailable).toBe(true);

    hide();
    show();
    expect(store.connectionUnavailable).toBe(true);
    hide();
    show();
    await vi.advanceTimersByTimeAsync(20_000);

    expect(store.connectionUnavailable).toBe(true);
    expect(toast.warning).toHaveBeenCalledTimes(1);
    store.destroy();
  });

  it("stays quiet on tab focus for a session denied realtime", async () => {
    const { store, show, hide } = await onPage();
    store.websocketClient.connectionStatus = "unauthorized";

    hide();
    show();
    await vi.advanceTimersByTimeAsync(20_000);

    expect(store.connectionUnavailable).toBe(false);
    expect(store.connectionPresentation).toBe("denied");
    expect(toast.warning).not.toHaveBeenCalled();
    store.destroy();
  });
});

describe("RealtimeStore reservoir freshness", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    api.getCurrentTherapyState.mockResolvedValue({ reservoir: 42 });
    api.apsGetAll.mockResolvedValue({ data: [] });
    api.emptyPage.mockResolvedValue({ data: [] });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  it("refreshes the reservoir after a devicestatus arrives on the realtime channel", async () => {
    const store = makeStore();
    expect(store.currentReservoir).toBeNull();

    store.handleCreate(deviceStatus("ds-1"));
    await vi.advanceTimersByTimeAsync(5_000);

    expect(api.getCurrentTherapyState).toHaveBeenCalledTimes(1);
    expect(store.currentReservoir).toBe(42);

    store.destroy();
  });

  it("clears the reservoir when the pump stops reporting a numeric level", async () => {
    const store = makeStore();
    api.getCurrentTherapyState.mockResolvedValue({ reservoir: undefined });

    store.currentReservoir = 12;
    store.handleCreate(deviceStatus("ds-1"));
    await vi.advanceTimersByTimeAsync(5_000);

    expect(store.currentReservoir).toBeNull();

    store.destroy();
  });

  it("absorbs a devicestatus burst into a single refresh", async () => {
    const store = makeStore();

    store.handleCreate(deviceStatus("ds-1"));
    store.handleCreate(deviceStatus("ds-2"));
    await vi.advanceTimersByTimeAsync(1_000);
    store.handleCreate(deviceStatus("ds-3"));
    await vi.advanceTimersByTimeAsync(5_000);

    expect(api.getCurrentTherapyState).toHaveBeenCalledTimes(1);
    expect(api.apsGetAll).toHaveBeenCalledTimes(1);

    // A devicestatus after the pending refresh has fired schedules a fresh one.
    store.handleCreate(deviceStatus("ds-4"));
    await vi.advanceTimersByTimeAsync(5_000);
    expect(api.getCurrentTherapyState).toHaveBeenCalledTimes(2);

    store.destroy();
  });

  it("refreshes the reservoir as part of a backfill", async () => {
    const store = makeStore();

    await store.performBackfillIfNeeded(true);

    expect(api.getCurrentTherapyState).toHaveBeenCalledTimes(1);
    expect(store.currentReservoir).toBe(42);

    store.destroy();
  });

  it("keeps the last reservoir value when the refresh fails", async () => {
    const store = makeStore();
    store.currentReservoir = 30;
    api.getCurrentTherapyState.mockRejectedValue(new Error("offline"));

    await store.performBackfillIfNeeded(true);

    expect(store.currentReservoir).toBe(30);

    store.destroy();
  });
});

describe("RealtimeStore direction", () => {
  it("passes the reported direction through", () => {
    const store = makeStore();
    store.entries = [
      { type: "sgv", mills: 1_000, sgv: 120, direction: "FortyFiveDown" },
    ];

    expect(store.direction).toBe("FortyFiveDown");

    store.destroy();
  });

  it.each([
    ["an entry with no direction", [{ type: "sgv", mills: 1_000, sgv: 120 }]],
    [
      "an empty direction",
      [{ type: "sgv", mills: 1_000, sgv: 120, direction: "" }],
    ],
    ["no entries at all", []],
  ])("reports no direction for %s rather than Flat", (_case, entries) => {
    const store = makeStore();
    store.entries = entries;

    expect(store.direction).toBe("");

    store.destroy();
  });
});

describe("RealtimeStore current reading", () => {
  it.each(["mbg", "cal"])(
    "is the newest sgv when a newer %s entry arrives",
    (type) => {
      const store = makeStore();
      store.entries = [
        { _id: "older-sgv", type: "sgv", sgv: 100, mills: 1_000 },
        {
          _id: "newest-sgv",
          type: "sgv",
          sgv: 130,
          mills: 2_000,
          direction: "SingleUp",
        },
        {
          _id: "meter",
          type,
          mbg: 250,
          mgdl: 250,
          mills: 3_000,
          direction: "DoubleDown",
        },
      ];

      expect(store.currentEntry?._id).toBe("newest-sgv");
      expect(store.direction).toBe("SingleUp");
      expect(store.bgDelta).toBe(30);

      store.destroy();
    }
  );
});

describe("RealtimeStore entry create batching", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("coalesces a connector catch-up burst into one entry update", async () => {
    const store = makeStore();

    for (let index = 0; index < 100; index += 1) {
      store.handleCreate({
        colName: "entries",
        doc: {
          _id: `reading-${index}`,
          type: "sgv",
          sgv: 100 + (index % 20),
          mills: 1_000_000 + index,
        },
      });
      await vi.advanceTimersByTimeAsync(2);
    }

    // Preserve the existing duplicate rule when IDs differ but the timestamp
    // and glucose value identify the same reading.
    store.handleCreate({
      colName: "entries",
      doc: {
        _id: "duplicate-reading-42",
        type: "sgv",
        sgv: 102,
        mills: 1_000_042,
      },
    });

    expect(store.entries).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(100);
    expect(store.entries).toHaveLength(100);
    expect(store.entries[0]._id).toBe("reading-99");

    store.destroy();
  });

  it("keeps later creates first when timestamps are equal", async () => {
    const store = makeStore();
    store.entries = [{ _id: "existing", type: "sgv", sgv: 90, mills: 1_000 }];

    store.handleCreate({
      colName: "entries",
      doc: { _id: "first-create", type: "sgv", sgv: 100, mills: 1_000 },
    });
    store.handleCreate({
      colName: "entries",
      doc: { _id: "second-create", type: "sgv", sgv: 110, mills: 1_000 },
    });

    await vi.advanceTimersByTimeAsync(100);

    expect(store.entries.map((entry) => entry._id)).toEqual([
      "second-create",
      "first-create",
      "existing",
    ]);

    store.destroy();
  });
});

describe("RealtimeStore events for a REST-backfilled reading", () => {
  // The store keys a backfilled reading on its uuid; the socket names the
  // same reading by its ObjectId form, as the legacy REST surface does.
  const backfilled = () =>
    sensorGlucoseToEntry({
      id: "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f",
      mgdl: 120,
      mills: 1_000,
    } as SensorGlucose);
  const socketDoc = {
    _id: "0198c2a41f3b7c2d9e556a1b",
    type: "sgv",
    sgv: 120,
    mills: 1_000,
  };

  it("removes the reading on delete", () => {
    const store = makeStore();
    store.entries = [backfilled(), { _id: "other", type: "sgv", sgv: 90, mills: 2_000 }];

    store.handleDelete({ colName: "entries", doc: socketDoc });

    expect(store.entries.map((entry) => entry._id)).toEqual(["other"]);
    store.destroy();
  });

  it("replaces the reading on update", () => {
    const store = makeStore();
    store.entries = [backfilled()];

    store.handleUpdate({
      colName: "entries",
      doc: { ...socketDoc, direction: "Flat" },
    });

    expect(store.entries).toHaveLength(1);
    expect(store.entries[0]).toMatchObject({ _id: "0198c2a41f3b7c2d9e556a1b", direction: "Flat" });
    store.destroy();
  });
});

describe("RealtimeStore sync progress", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function syncEvent(
    connectorId: string,
    phase: SyncProgressEvent["phase"],
    messageType: SyncProgressEvent["messageType"]
  ): SyncProgressEvent {
    return {
      connectorId,
      connectorName: connectorId,
      phase,
      errorMessage: null,
      timestamp: new Date().toISOString(),
      messageType,
      messageParams: null,
    };
  }

  function emit(store: TestStore, event: SyncProgressEvent): void {
    store.websocketClient.eventHandlers.syncProgress?.(event);
  }

  it("holds an in-progress sync on screen indefinitely", () => {
    const store = makeStore();

    emit(store, syncEvent("glooko", "Syncing", "FetchingData"));
    vi.advanceTimersByTime(60_000);

    expect(store.syncProgressByConnector.glooko?.phase).toBe("Syncing");

    store.destroy();
  });

  it.each(["Completed", "Failed"] as const)(
    "clears a %s sync after the linger window",
    (phase) => {
      const store = makeStore();

      emit(store, syncEvent("glooko", "Syncing", "FetchingData"));
      emit(
        store,
        syncEvent(
          "glooko",
          phase,
          phase === "Completed" ? "SyncComplete" : "SyncFailed"
        )
      );

      // Still visible while the outcome lingers.
      vi.advanceTimersByTime(1_999);
      expect(store.syncProgressByConnector.glooko?.phase).toBe(phase);

      vi.advanceTimersByTime(1);
      expect(store.syncProgressByConnector.glooko).toBeUndefined();

      store.destroy();
    }
  );

  it("keeps a new run that started inside the previous run's linger window", () => {
    const store = makeStore();

    emit(store, syncEvent("glooko", "Completed", "SyncComplete"));
    vi.advanceTimersByTime(1_000);
    emit(store, syncEvent("glooko", "Syncing", "FetchingData"));
    vi.advanceTimersByTime(1_000);

    expect(store.syncProgressByConnector.glooko?.phase).toBe("Syncing");

    store.destroy();
  });

  it("clears only the connector that finished", () => {
    const store = makeStore();

    emit(store, syncEvent("glooko", "Syncing", "FetchingData"));
    emit(store, syncEvent("dexcom", "Completed", "SyncComplete"));
    vi.advanceTimersByTime(2_000);

    expect(store.syncProgressByConnector.dexcom).toBeUndefined();
    expect(store.syncProgressByConnector.glooko?.phase).toBe("Syncing");

    store.destroy();
  });
});

describe("RealtimeStore tracker updates", () => {
  function trackerInstance(
    id: string,
    overrides: Partial<TrackerInstanceDto> = {}
  ): TrackerInstanceDto {
    return {
      id,
      definitionId: "definition-1",
      definitionName: "Infusion Site",
      startedAt: "2026-09-20T08:00:00Z",
      ageHours: 24,
      isActive: true,
      ...overrides,
    };
  }

  function emit(store: TestStore, event: TrackerUpdateEvent): void {
    store.websocketClient.eventHandlers.trackerUpdate?.(event);
  }

  it("applies an ack from another device to the stored instance", () => {
    const store = makeStore();
    store.trackerInstances = [
      trackerInstance("site"),
      trackerInstance("sensor"),
    ];

    const acked = trackerInstance("site", {
      ageHours: 25,
      lastAckedAt: "2026-09-21T09:00:00Z",
      ackSnoozeMins: 30,
    });
    emit(store, { action: "ack", instance: acked });

    expect(store.trackerInstances).toEqual([acked, trackerInstance("sensor")]);

    store.destroy();
  });

  it("ignores an ack for an instance it does not hold", () => {
    const store = makeStore();
    store.trackerInstances = [trackerInstance("site")];

    emit(store, { action: "ack", instance: trackerInstance("unknown") });

    expect(store.trackerInstances).toEqual([trackerInstance("site")]);

    store.destroy();
  });

  it("drops a completed instance from the active list", () => {
    const store = makeStore();
    store.trackerInstances = [
      trackerInstance("site"),
      trackerInstance("sensor"),
    ];

    emit(store, {
      action: "complete",
      instance: trackerInstance("site", {
        completedAt: "2026-09-21T10:00:00Z",
        isActive: false,
      }),
    });

    expect(store.trackerInstances.map((i) => i.id)).toEqual(["sensor"]);

    store.destroy();
  });
});

describe("RealtimeStore merging", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    api.getCurrentTherapyState.mockResolvedValue({ reservoir: null });
    api.apsGetAll.mockResolvedValue({ data: [] });
    api.emptyPage.mockResolvedValue({ data: [] });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  it("keeps a reading already held under another id, and adds only the new one", async () => {
    const store = makeStore();
    store.entries = [{ _id: "socket-id", type: "sgv", sgv: 120, mills: 1_000 }];

    store.handleDataUpdate({
      data: [
        { _id: "uuid-id", type: "sgv", sgv: 120, mills: 1_000 },
        { _id: "socket-id", type: "sgv", sgv: 99, mills: 2_000 },
        { _id: "fresh", type: "sgv", sgv: 130, mills: 3_000 },
        { _id: "fresh", type: "sgv", sgv: 130, mills: 3_000 },
      ],
    });

    expect(store.entries.map((entry) => entry._id)).toEqual(["fresh", "socket-id"]);
    store.destroy();
  });

  it("backfills only boluses it does not already hold", async () => {
    const store = makeStore();
    const now = Date.now();
    store.boluses = [{ id: "b1", mills: now - 1_000 }] as typeof store.boluses;
    const pages = [{ id: "b1", mills: now - 1_000 }, { id: "b2", mills: now - 500 }];
    api.emptyPage.mockImplementation(async (_from, _to, limit) => ({ data: limit === 500 ? pages : [] }));

    await store.performBackfillIfNeeded(true);

    expect(store.boluses.map((bolus) => bolus.id)).toEqual(["b2", "b1"]);
    store.destroy();
  });
});

describe("loadInitialGlucose", () => {
  const FROM = "2026-10-04T00:00:00.000Z";

  function clientWith(getAll: ReturnType<typeof vi.fn>) {
    return { sensorGlucose: { getAll } } as unknown as Parameters<typeof loadInitialGlucose>[0];
  }

  const readings = (...mgdl: number[]) => ({
    data: mgdl.map((value, i) => ({ id: `r${value}`, mills: 1000 - i, mgdl: value })),
  });

  it("asks only for the given window when it holds a full recent list", async () => {
    const window = readings(100, 101, 102, 103, 104, 105).data.slice(0, RECENT_READINGS);
    const getAll = vi.fn().mockResolvedValue({ data: window });

    const entries = await loadInitialGlucose(clientWith(getAll), FROM);

    expect(getAll).toHaveBeenCalledTimes(1);
    expect(getAll).toHaveBeenCalledWith(FROM, undefined, 1000);
    expect(entries).toHaveLength(RECENT_READINGS);
  });

  it("tops up a window holding a single reading so the delta has a previous reading", async () => {
    const getAll = vi
      .fn()
      .mockResolvedValueOnce(readings(120))
      .mockResolvedValueOnce(readings(120, 115, 110));

    const entries = await loadInitialGlucose(clientWith(getAll), FROM);

    expect(getAll).toHaveBeenLastCalledWith(undefined, undefined, RECENT_READINGS);
    expect(entries.map((e) => e.sgv)).toEqual([120, 115, 110]);
  });

  it("keeps every window reading when the newest include ones outside it", async () => {
    const getAll = vi
      .fn()
      .mockResolvedValueOnce(readings(120, 118))
      .mockResolvedValueOnce(readings(130, 125, 122, 120, 118));

    const entries = await loadInitialGlucose(clientWith(getAll), FROM);

    expect(entries.map((e) => e.sgv).sort()).toEqual([118, 120, 122, 125, 130]);
  });

  it("falls back to the newest readings when the window is empty", async () => {
    const getAll = vi.fn().mockResolvedValueOnce({ data: [] }).mockResolvedValueOnce(readings(140));

    const entries = await loadInitialGlucose(clientWith(getAll), FROM);

    expect(entries.map((e) => e.sgv)).toEqual([140]);
  });

  it("returns no readings when the request fails", async () => {
    const getAll = vi.fn().mockRejectedValue(new Error("offline"));

    await expect(loadInitialGlucose(clientWith(getAll), FROM)).resolves.toEqual([]);
  });
});
