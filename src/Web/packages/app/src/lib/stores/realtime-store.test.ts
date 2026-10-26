import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

const api = vi.hoisted(() => ({
  getCurrentTherapyState: vi.fn(),
  apsGetAll: vi.fn(),
  emptyPage: vi.fn(),
  getProfileSummary: vi.fn(),
  emptyList: vi.fn(),
  getTrackerDefinitions: vi.fn(() => Promise.resolve([] as unknown[])),
  getActiveTrackerInstances: vi.fn(() => Promise.resolve([] as unknown[])),
  getLegacyFallbacks: vi.fn(() => Promise.resolve({ fallbacks: [], covered: [] } as unknown)),
}));

vi.mock("$lib/api/browser-clients", () => ({
  currentTherapyStateClient: () => ({ getCurrentTherapyState: api.getCurrentTherapyState }),
  apsSnapshotClient: () => ({ getAll: api.apsGetAll }),
  sensorGlucoseClient: () => ({ getAll: api.emptyPage }),
  bolusClient: () => ({ getAll: api.emptyPage }),
  nutritionClient: () => ({ getCarbIntakes: api.emptyPage }),
  bgCheckClient: () => ({ getAll: api.emptyPage }),
  noteClient: () => ({ getAll: api.emptyPage }),
  deviceEventClient: () => ({ getAll: api.emptyPage }),
  profileClient: () => ({ getProfileSummary: api.getProfileSummary }),
  trackersClient: () => ({
    getDefinitions: api.getTrackerDefinitions,
    getActiveInstances: api.getActiveTrackerInstances,
  }),
  notificationsClient: () => ({ getNotifications: api.emptyList }),
  trackerLegacyFallbacksClient: () => ({ getLegacyFallbacks: api.getLegacyFallbacks }),
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
import { GLUCOSE_WINDOW_HOURS, initialReadWindows } from "./initial-reads";
import type {
  ConnectionInfo,
  StorageEvent,
  SyncProgressEvent,
  TrackerDefinitionUpdateEvent,
  TrackerUpdateEvent,
  WebSocketConnectionStatus,
} from "$lib/websocket/types";
import {
  SyncPhase,
  LegacyConsumableKind,
  TrackerRealtimeAction,
  type InAppNotificationDto,
  type LegacyCoverageDto,
  type LegacyFallbackDto,
  type LegacyFallbacksDto,
  type SensorGlucose,
  type TrackerDefinitionDto,
  type TrackerInstanceDto,
} from "$lib/api";

/** The realtime/backfill entry points, which the class keeps private. */
interface StoreInternals {
  handleCreate(event: StorageEvent): void;
  handleUpdate(event: StorageEvent): void;
  handleDelete(event: StorageEvent): void;
  performBackfillIfNeeded(force?: boolean): Promise<void>;
  handleDataUpdate(event: { data: unknown[] }): void;
  backfillCursor: number;
  lastEventReceived: number;
  handleVisibilityChange: (() => void) | null;
  websocketClient: {
    connectionStatus: WebSocketConnectionStatus;
    connect(): void;
    ensureConnected(): void;
    prefetchTicket(): void;
    eventHandlers: {
      connect?: (info: ConnectionInfo) => void;
      disconnect?: (reason: string) => void;
      connect_error?: (error: Error) => void;
      syncProgress?: (event: SyncProgressEvent) => void;
      trackerUpdate?: (event: TrackerUpdateEvent) => void;
      trackerDefinitionUpdate?: (event: TrackerDefinitionUpdateEvent) => void;
      notificationUpdated?: (notification: InAppNotificationDto) => void;
      notificationArchived?: (notification: InAppNotificationDto) => void;
    };
  };
}

type TestStore = StoreInternals &
  Pick<
    RealtimeStore,
    | "connectionUnavailable"
    | "connectionPresentation"
    | "currentReservoir"
    | "chartThresholds"
    | "entries"
    | "boluses"
    | "notes"
    | "currentEntry"
    | "bgDelta"
    | "previousBG"
    | "direction"
    | "syncProgressByConnector"
    | "trackerInstances"
    | "trackerDefinitions"
    | "legacyFallbacks"
    | "legacyCoverage"
    | "apsSnapshots"
    | "carbIntakes"
    | "treatmentRevision"
    | "profile"
    | "isReady"
    | "currentReadingLoaded"
    | "destroy"
    | "refreshTrackers"
    | "initialize"
    | "inAppNotifications"
    | "removeNotification"
  >;

/** Store instance with an empty socket URL, so nothing connects. */
function makeStore(url = ""): TestStore {
  const store = new RealtimeStore({
    url,
    reconnectAttempts: 0,
    reconnectDelay: 0,
    maxReconnectDelay: 0,
    pingTimeout: 0,
    pingInterval: 0,
  });
  return store as unknown as TestStore;
}

function fallback(kind: LegacyConsumableKind, ageHours: number): LegacyFallbackDto {
  return { kind, ageHours };
}

function read(fallbacks: LegacyFallbackDto[], covered: LegacyCoverageDto[] = []): LegacyFallbacksDto {
  return { fallbacks, covered };
}

function internals(store: TestStore): { readLegacyFallbacks(): Promise<void> } {
  return store as unknown as { readLegacyFallbacks(): Promise<void> };
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

describe("RealtimeStore chart thresholds", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    api.apsGetAll.mockResolvedValue({ data: [] });
    api.emptyPage.mockResolvedValue({ data: [] });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  it("keeps the chart thresholds from the therapy state", async () => {
    const thresholds = { low: 70, high: 180, veryLow: 54, veryHigh: 250, targetLow: 90, targetHigh: 140 };
    api.getCurrentTherapyState.mockResolvedValue({ reservoir: 42, thresholds });
    const store = makeStore();
    expect(store.chartThresholds).toBeNull();

    await store.performBackfillIfNeeded(true);

    expect(store.chartThresholds).toEqual(thresholds);

    store.destroy();
  });

  it("keeps the last chart thresholds when the refresh fails", async () => {
    const store = makeStore();
    const thresholds = { low: 70, high: 180 };
    store.chartThresholds = thresholds;
    api.getCurrentTherapyState.mockRejectedValue(new Error("offline"));

    await store.performBackfillIfNeeded(true);

    expect(store.chartThresholds).toBe(thresholds);

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

describe("RealtimeStore delta", () => {
  it("is null for a lone reading that carries no delta", () => {
    const store = makeStore();
    store.entries = [{ _id: "only", type: "sgv", sgv: 142, mills: 1_000 }];

    expect(store.previousBG).toBeNull();
    expect(store.bgDelta).toBeNull();

    store.destroy();
  });

  it("is the delta the lone reading carries", () => {
    const store = makeStore();
    store.entries = [
      { _id: "only", type: "sgv", sgv: 142, mills: 1_000, delta: 6 },
    ];

    expect(store.bgDelta).toBe(6);

    store.destroy();
  });

  it("is the gap to the previous reading when none is carried", () => {
    const store = makeStore();
    store.entries = [
      { _id: "older", type: "sgv", sgv: 142, mills: 1_000 },
      { _id: "newer", type: "sgv", sgv: 130, mills: 2_000 },
    ];

    expect(store.bgDelta).toBe(-12);

    store.destroy();
  });

  it("is null with no readings", () => {
    const store = makeStore();
    store.entries = [];

    expect(store.bgDelta).toBeNull();

    store.destroy();
  });
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

describe("RealtimeStore events for a reading two uploaders report", () => {
  // Prelude and AAPS each upload the same reading under their own ids; AAPS then
  // revises its value to Prelude's, so the two rows share a reading.
  const prelude = { _id: "prelude-reading", type: "sgv", sgv: 201.8, mills: 1_000 };
  const aaps = { _id: "aaps-reading", type: "sgv", sgv: 202, mills: 1_000 };

  it("updates the row with the update's id, not the other uploader's row with its reading", () => {
    const store = makeStore();
    store.handleDataUpdate({ data: [prelude] });
    store.handleDataUpdate({ data: [aaps] });

    store.handleUpdate({ colName: "entries", doc: { ...aaps, sgv: 201.8 } });

    expect(store.entries.map((entry) => [entry._id, entry.sgv])).toEqual([
      ["prelude-reading", 201.8],
      ["aaps-reading", 201.8],
    ]);
    store.destroy();
  });

  it("deletes only the row with the delete's id", () => {
    const store = makeStore();
    store.entries = [prelude, { ...aaps, sgv: 201.8 }];

    store.handleDelete({ colName: "entries", doc: { ...aaps, sgv: 201.8 } });

    expect(store.entries.map((entry) => entry._id)).toEqual(["prelude-reading"]);
    store.destroy();
  });
});

describe("RealtimeStore native record broadcasts", () => {
  const NOW = Date.parse("2026-10-20T12:00:00Z");
  const minutesAgo = (minutes: number) => NOW - minutes * 60_000;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  const care = (recordType: string, doc: object): StorageEvent => ({
    colName: "care",
    recordType,
    doc,
  });

  it("adds a created bolus newest first", () => {
    const store = makeStore();
    store.boluses = [{ id: "older", insulin: 1, mills: minutesAgo(60) }];

    store.handleCreate(care("bolus", { id: "b1", insulin: 2.5, mills: minutesAgo(5) }));

    expect(store.boluses.map((b) => b.id)).toEqual(["b1", "older"]);
    expect(store.boluses[0].insulin).toBe(2.5);
    store.destroy();
  });

  it("replaces an updated bolus by id rather than adding a second copy", () => {
    const store = makeStore();
    store.boluses = [{ id: "b1", insulin: 2.5, mills: minutesAgo(5) }];

    store.handleUpdate(care("bolus", { id: "b1", insulin: 3, mills: minutesAgo(5) }));

    expect(store.boluses).toEqual([{ id: "b1", insulin: 3, mills: minutesAgo(5) }]);
    store.destroy();
  });

  it.each(["create", "update", "delete"] as const)(
    "bumps the treatment revision on a record %s, which an in-place edit gives no other signal of",
    (action) => {
      const store = makeStore();
      store.boluses = [{ id: "b1", insulin: 2.5, mills: minutesAgo(5) }];
      const before = store.treatmentRevision;
      const event = { ...care("bolus", { id: "b1", insulin: 3, mills: minutesAgo(5) }), id: "b1" };

      if (action === "create") store.handleCreate(event);
      else if (action === "update") store.handleUpdate(event);
      else store.handleDelete(event);

      expect(store.treatmentRevision).toBe(before + 1);
      store.destroy();
    }
  );

  it("removes a deleted bolus by the id the delete carries", () => {
    const store = makeStore();
    store.boluses = [
      { id: "b1", insulin: 2.5, mills: minutesAgo(5) },
      { id: "b2", insulin: 1, mills: minutesAgo(10) },
    ];

    store.handleDelete({ colName: "care", recordType: "bolus", id: "b1", doc: {} });

    expect(store.boluses.map((b) => b.id)).toEqual(["b2"]);
    store.destroy();
  });

  it("creates, updates and deletes an APS snapshot by id", () => {
    const store = makeStore();
    const device = (doc: object): StorageEvent => ({ colName: "device", recordType: "apsSnapshot", doc });

    store.handleCreate(device({ id: "a1", iob: 1.2, mills: minutesAgo(5) }));
    expect(store.apsSnapshots).toEqual([{ id: "a1", iob: 1.2, mills: minutesAgo(5) }]);

    store.handleUpdate(device({ id: "a1", iob: 0.8, mills: minutesAgo(5) }));
    expect(store.apsSnapshots).toEqual([{ id: "a1", iob: 0.8, mills: minutesAgo(5) }]);

    store.handleDelete({ colName: "device", recordType: "apsSnapshot", id: "a1", doc: {} });
    expect(store.apsSnapshots).toEqual([]);
    store.destroy();
  });

  it("merges each record type into its own array only", () => {
    const store = makeStore();

    store.handleCreate(care("carbIntake", { id: "c1", carbs: 30, mills: minutesAgo(5) }));

    expect(store.carbIntakes.map((c) => c.id)).toEqual(["c1"]);
    expect(store.boluses).toEqual([]);
    store.destroy();
  });

  it("ignores a created record stamped before the store's window", () => {
    const store = makeStore();

    store.handleCreate(care("bolus", { id: "old", insulin: 2, mills: minutesAgo(25 * 60) }));

    expect(store.boluses).toEqual([]);
    store.destroy();
  });

  it("drops a held record an update moves before the store's window", () => {
    const store = makeStore();
    store.boluses = [{ id: "b1", insulin: 2, mills: minutesAgo(5) }];

    store.handleUpdate(care("bolus", { id: "b1", insulin: 2, mills: minutesAgo(25 * 60) }));

    expect(store.boluses).toEqual([]);
    store.destroy();
  });

  it("leaves the arrays alone for a record type the store does not hold", () => {
    const store = makeStore();
    const boluses = store.boluses;

    store.handleCreate({ colName: "glucose", recordType: "sensorGlucose", doc: { id: "s1", mgdl: 120, mills: minutesAgo(1) } });

    expect(store.boluses).toBe(boluses);
    expect(store.entries).toEqual([]);
    store.destroy();
  });

  it("does not bump the treatment revision for a glucose reading or a record outside the window", () => {
    const store = makeStore();
    const before = store.treatmentRevision;

    store.handleCreate({ colName: "glucose", recordType: "sensorGlucose", doc: { id: "s1", mgdl: 120, mills: minutesAgo(1) } });
    store.handleCreate(care("bolus", { id: "old", insulin: 1, mills: minutesAgo(60 * 48) }));

    expect(store.treatmentRevision).toBe(before);
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

    emit(store, syncEvent("glooko", SyncPhase.Syncing, "FetchingData"));
    vi.advanceTimersByTime(60_000);

    expect(store.syncProgressByConnector.glooko?.phase).toBe(SyncPhase.Syncing);

    store.destroy();
  });

  it.each([SyncPhase.Completed, SyncPhase.Failed] as const)(
    "clears a %s sync after the linger window",
    (phase) => {
      const store = makeStore();

      emit(store, syncEvent("glooko", SyncPhase.Syncing, "FetchingData"));
      emit(
        store,
        syncEvent(
          "glooko",
          phase,
          phase === SyncPhase.Completed ? "SyncComplete" : "SyncFailed"
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

    emit(store, syncEvent("glooko", SyncPhase.Completed, "SyncComplete"));
    vi.advanceTimersByTime(1_000);
    emit(store, syncEvent("glooko", SyncPhase.Syncing, "FetchingData"));
    vi.advanceTimersByTime(1_000);

    expect(store.syncProgressByConnector.glooko?.phase).toBe(SyncPhase.Syncing);

    store.destroy();
  });

  it("clears only the connector that finished", () => {
    const store = makeStore();

    emit(store, syncEvent("glooko", SyncPhase.Syncing, "FetchingData"));
    emit(store, syncEvent("dexcom", SyncPhase.Completed, "SyncComplete"));
    vi.advanceTimersByTime(2_000);

    expect(store.syncProgressByConnector.dexcom).toBeUndefined();
    expect(store.syncProgressByConnector.glooko?.phase).toBe(SyncPhase.Syncing);

    store.destroy();
  });
});

describe("RealtimeStore optimistic notification removal", () => {
  const notification = (id: string, readAt?: string): InAppNotificationDto => ({
    id,
    title: id,
    createdAt: "2026-09-21T08:00:00Z",
    readAt,
  });

  function emit(
    store: TestStore,
    event: "notificationUpdated" | "notificationArchived",
    n: InAppNotificationDto
  ): void {
    store.websocketClient.eventHandlers[event]?.(n);
  }

  it("puts a removed notification back when restored", () => {
    const store = makeStore();
    store.inAppNotifications = [notification("a"), notification("b")];

    store.removeNotification("a").restore();

    expect(store.inAppNotifications.map((n) => n.id).sort()).toEqual(["a", "b"]);
    store.destroy();
  });

  it("does not put back a notification whose archive arrived before the failure", () => {
    const store = makeStore();
    store.inAppNotifications = [notification("a")];

    const removal = store.removeNotification("a");
    emit(store, "notificationArchived", notification("a"));
    removal.restore();

    expect(store.inAppNotifications).toEqual([]);
    store.destroy();
  });

  it("keeps an update for a notification being removed off the list, and restores that update", () => {
    const store = makeStore();
    store.inAppNotifications = [notification("a")];

    const removal = store.removeNotification("a");
    const read = notification("a", "2026-09-21T09:00:00Z");
    emit(store, "notificationUpdated", read);
    expect(store.inAppNotifications).toEqual([]);

    removal.restore();
    expect(store.inAppNotifications).toEqual([read]);
    store.destroy();
  });

  it("lets updates add the notification again once the removal has settled", () => {
    const store = makeStore();
    store.inAppNotifications = [notification("a")];

    store.removeNotification("a").settle();
    emit(store, "notificationUpdated", notification("a"));

    expect(store.inAppNotifications.map((n) => n.id)).toEqual(["a"]);
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
    emit(store, { action: TrackerRealtimeAction.Ack, instance: acked });

    expect(store.trackerInstances).toEqual([acked, trackerInstance("sensor")]);

    store.destroy();
  });

  it("restores a reopened instance without announcing a new start", () => {
    const store = makeStore();
    store.trackerInstances = [trackerInstance("sensor")];
    vi.mocked(toast.info).mockClear();

    emit(store, { action: TrackerRealtimeAction.Reopen, instance: trackerInstance("site") });
    emit(store, { action: TrackerRealtimeAction.Reopen, instance: trackerInstance("site") });

    expect(store.trackerInstances).toEqual([
      trackerInstance("site"),
      trackerInstance("sensor"),
    ]);
    expect(toast.info).not.toHaveBeenCalled();

    store.destroy();
  });

  it("ignores an ack for an instance it does not hold", () => {
    const store = makeStore();
    store.trackerInstances = [trackerInstance("site")];

    emit(store, { action: TrackerRealtimeAction.Ack, instance: trackerInstance("unknown") });

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
      action: TrackerRealtimeAction.Complete,
      instance: trackerInstance("site", {
        completedAt: "2026-09-21T10:00:00Z",
        isActive: false,
      }),
    });

    expect(store.trackerInstances.map((i) => i.id)).toEqual(["sensor"]);

    store.destroy();
  });
});

describe("RealtimeStore tracker definition updates", () => {
  const definition = (id: string, name = id): TrackerDefinitionDto => ({ id, name });
  const instance = (id: string, definitionId: string, definitionName = definitionId): TrackerInstanceDto => ({
    id,
    definitionId,
    definitionName,
  });

  beforeEach(() => {
    api.getLegacyFallbacks.mockResolvedValue(read([]));
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  function emit(store: TestStore, definitionId: string): void {
    store.websocketClient.eventHandlers.trackerDefinitionUpdate?.({ definitionId });
  }

  async function settled(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  it("carries a renamed definition onto its running instance", async () => {
    const store = makeStore();
    store.trackerDefinitions = [definition("site", "Site")];
    store.trackerInstances = [instance("run", "site", "Site")];
    api.getTrackerDefinitions.mockResolvedValue([definition("site", "Pod")]);
    api.getActiveTrackerInstances.mockResolvedValue([instance("run", "site", "Pod")]);

    emit(store, "site");
    await settled();

    expect(store.trackerDefinitions.map((d) => d.name)).toEqual(["Pod"]);
    expect(store.trackerInstances.map((i) => i.definitionName)).toEqual(["Pod"]);

    store.destroy();
  });

  it("clears the last running instance when it was completed elsewhere", async () => {
    const store = makeStore();
    store.trackerDefinitions = [definition("site")];
    store.trackerInstances = [instance("run", "site")];
    api.getTrackerDefinitions.mockResolvedValue([definition("site")]);
    api.getActiveTrackerInstances.mockResolvedValue([]);

    emit(store, "site");
    await settled();

    expect(store.trackerInstances).toEqual([]);

    store.destroy();
  });

  it("clears the definitions and instances when the last definition is deleted", async () => {
    const store = makeStore();
    store.trackerDefinitions = [definition("site")];
    store.trackerInstances = [instance("run", "site")];
    api.getTrackerDefinitions.mockResolvedValue([]);
    api.getActiveTrackerInstances.mockResolvedValue([]);

    emit(store, "site");
    await settled();

    expect(store.trackerDefinitions).toEqual([]);
    expect(store.trackerInstances).toEqual([]);

    store.destroy();
  });

  it("keeps only the latest read when two overlap", async () => {
    const store = makeStore();
    let resolveFirst: (value: TrackerDefinitionDto[]) => void = () => {};
    api.getTrackerDefinitions
      .mockReturnValueOnce(new Promise<TrackerDefinitionDto[]>((resolve) => (resolveFirst = resolve)))
      .mockResolvedValueOnce([definition("sensor")]);
    api.getActiveTrackerInstances.mockResolvedValue([]);

    emit(store, "site");
    emit(store, "sensor");
    await settled();
    resolveFirst([definition("site")]);
    await settled();

    expect(store.trackerDefinitions.map((d) => d.id)).toEqual(["sensor"]);

    store.destroy();
  });

  it("keeps the definitions and instances when the read fails", async () => {
    const store = makeStore();
    store.trackerDefinitions = [definition("site")];
    store.trackerInstances = [instance("run", "site")];
    api.getTrackerDefinitions.mockRejectedValue(new Error("offline"));
    api.getActiveTrackerInstances.mockResolvedValue([]);
    vi.spyOn(console, "error").mockImplementation(() => {});

    emit(store, "site");
    await settled();

    expect(store.trackerDefinitions.map((d) => d.id)).toEqual(["site"]);
    expect(store.trackerInstances.map((i) => i.id)).toEqual(["run"]);

    store.destroy();
  });

  it("applies an earlier read when a later one fails", async () => {
    const store = makeStore();
    let resolveFirst: (value: TrackerDefinitionDto[]) => void = () => {};
    api.getTrackerDefinitions
      .mockReturnValueOnce(new Promise<TrackerDefinitionDto[]>((resolve) => (resolveFirst = resolve)))
      .mockRejectedValueOnce(new Error("offline"));
    api.getActiveTrackerInstances.mockResolvedValue([]);
    vi.spyOn(console, "error").mockImplementation(() => {});

    emit(store, "site");
    emit(store, "sensor");
    await settled();
    resolveFirst([definition("site")]);
    await settled();

    expect(store.trackerDefinitions.map((d) => d.id)).toEqual(["site"]);

    store.destroy();
  });

  it("reads again when a run completes while a read is in flight", async () => {
    const store = makeStore();
    store.trackerDefinitions = [definition("site")];
    store.trackerInstances = [instance("run", "site")];
    let resolveStale: (value: TrackerInstanceDto[]) => void = () => {};
    api.getTrackerDefinitions.mockResolvedValue([definition("site")]);
    api.getActiveTrackerInstances
      .mockReturnValueOnce(new Promise<TrackerInstanceDto[]>((resolve) => (resolveStale = resolve)))
      .mockResolvedValueOnce([]);

    const refreshing = store.refreshTrackers();
    await settled();
    store.websocketClient.eventHandlers.trackerUpdate?.({
      action: TrackerRealtimeAction.Complete,
      instance: instance("run", "site"),
    });
    resolveStale([instance("run", "site")]);
    await refreshing;

    expect(store.trackerInstances).toEqual([]);
    expect(api.getActiveTrackerInstances).toHaveBeenCalledTimes(2);

    store.destroy();
  });

  it("reads again when a run starts while a read is in flight", async () => {
    const store = makeStore();
    store.trackerDefinitions = [definition("site")];
    let resolveStale: (value: TrackerInstanceDto[]) => void = () => {};
    api.getTrackerDefinitions.mockResolvedValue([definition("site")]);
    api.getActiveTrackerInstances
      .mockReturnValueOnce(new Promise<TrackerInstanceDto[]>((resolve) => (resolveStale = resolve)))
      .mockResolvedValueOnce([instance("run", "site")]);

    const refreshing = store.refreshTrackers();
    await settled();
    store.websocketClient.eventHandlers.trackerUpdate?.({
      action: TrackerRealtimeAction.Create,
      instance: instance("run", "site"),
    });
    resolveStale([]);
    await refreshing;

    expect(store.trackerInstances.map((i) => i.id)).toEqual(["run"]);
    expect(api.getActiveTrackerInstances).toHaveBeenCalledTimes(2);

    store.destroy();
  });

  it("swaps a converted fallback for the tracker it became", async () => {
    const store = makeStore();
    store.legacyFallbacks = [fallback(LegacyConsumableKind.Site, 50)];
    api.getTrackerDefinitions.mockResolvedValue([definition("site")]);
    api.getActiveTrackerInstances.mockResolvedValue([instance("run", "site")]);
    api.getLegacyFallbacks.mockResolvedValue(
      read([], [{ kind: LegacyConsumableKind.Site, definitionIds: ["site"] }])
    );

    emit(store, "site");
    await settled();

    expect(store.legacyFallbacks).toEqual([]);
    expect(store.legacyCoverage).toEqual([{ kind: LegacyConsumableKind.Site, definitionIds: ["site"] }]);
    expect(store.trackerInstances.map((i) => i.id)).toEqual(["run"]);

    store.destroy();
  });

  it("keeps the definitions and instances when only the fallback read fails", async () => {
    const store = makeStore();
    store.legacyFallbacks = [fallback(LegacyConsumableKind.Site, 50)];
    api.getTrackerDefinitions.mockResolvedValue([definition("site", "Pod")]);
    api.getActiveTrackerInstances.mockResolvedValue([instance("run", "site", "Pod")]);
    api.getLegacyFallbacks.mockRejectedValue(new Error("offline"));
    vi.spyOn(console, "error").mockImplementation(() => {});

    emit(store, "site");
    await settled();

    expect(store.trackerDefinitions.map((d) => d.name)).toEqual(["Pod"]);
    expect(store.trackerInstances.map((i) => i.id)).toEqual(["run"]);
    expect(store.legacyFallbacks.map((f) => f.ageHours)).toEqual([50]);

    store.destroy();
  });

  it("keeps a fallback read that started later than the definition update's", async () => {
    const store = makeStore();
    let resolveCombined: (value: LegacyFallbacksDto) => void = () => {};
    api.getLegacyFallbacks
      .mockReturnValueOnce(new Promise<LegacyFallbacksDto>((resolve) => (resolveCombined = resolve)))
      .mockResolvedValueOnce(read([fallback(LegacyConsumableKind.Sensor, 2)]));

    emit(store, "site");
    void internals(store).readLegacyFallbacks();
    await settled();
    resolveCombined(read([fallback(LegacyConsumableKind.Sensor, 200)]));
    await settled();

    expect(store.legacyFallbacks.map((f) => f.ageHours)).toEqual([2]);

    store.destroy();
  });

  it("applies the definition update's fallbacks when a later fallback read failed", async () => {
    const store = makeStore();
    let resolveCombined: (value: LegacyFallbacksDto) => void = () => {};
    api.getLegacyFallbacks
      .mockReturnValueOnce(new Promise<LegacyFallbacksDto>((resolve) => (resolveCombined = resolve)))
      .mockRejectedValueOnce(new Error("offline"));
    vi.spyOn(console, "error").mockImplementation(() => {});

    emit(store, "site");
    void internals(store).readLegacyFallbacks();
    await settled();
    resolveCombined(read([], [{ kind: LegacyConsumableKind.Site, definitionIds: ["site"] }]));
    await settled();

    expect(store.legacyFallbacks).toEqual([]);
    expect(store.legacyCoverage.map((c) => c.kind)).toEqual([LegacyConsumableKind.Site]);

    store.destroy();
  });
});

describe("RealtimeStore legacy fallbacks", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  const deviceEvent = (id: string): StorageEvent => ({
    colName: "care",
    recordType: "deviceEvent",
    doc: { id, mills: Date.now(), eventType: "SiteChange" },
  });

  it("re-reads the fallbacks once after a burst of device events", async () => {
    const store = makeStore();
    store.legacyFallbacks = [fallback(LegacyConsumableKind.Site, 70)];
    api.getLegacyFallbacks.mockResolvedValue(read([fallback(LegacyConsumableKind.Site, 0)]));

    store.handleCreate(deviceEvent("d1"));
    store.handleCreate(deviceEvent("d2"));
    await vi.runOnlyPendingTimersAsync();

    expect(api.getLegacyFallbacks).toHaveBeenCalledTimes(1);
    expect(store.legacyFallbacks.map((f) => f.ageHours)).toEqual([0]);

    store.destroy();
  });

  it("keeps the fallbacks when the read fails", async () => {
    const store = makeStore();
    store.legacyFallbacks = [fallback(LegacyConsumableKind.Site, 70)];
    api.getLegacyFallbacks.mockRejectedValue(new Error("offline"));
    vi.spyOn(console, "error").mockImplementation(() => {});

    store.handleCreate(deviceEvent("d1"));
    await vi.runOnlyPendingTimersAsync();

    expect(store.legacyFallbacks.map((f) => f.ageHours)).toEqual([70]);

    store.destroy();
  });

  it("drops a pending fallback re-read on destroy", async () => {
    const store = makeStore();

    store.handleCreate(deviceEvent("d1"));
    store.destroy();
    await vi.runOnlyPendingTimersAsync();

    expect(api.getLegacyFallbacks).not.toHaveBeenCalled();
  });

  it("re-reads the fallbacks at once when a run of a new definition starts", async () => {
    const store = makeStore();
    store.legacyFallbacks = [fallback(LegacyConsumableKind.Site, 70)];
    api.getLegacyFallbacks.mockResolvedValue(read([]));

    store.websocketClient.eventHandlers.trackerUpdate?.({
      action: TrackerRealtimeAction.Create,
      instance: { id: "run", definitionId: "site", definitionName: "Infusion Site" },
    });
    await vi.advanceTimersByTimeAsync(0);

    expect(store.legacyFallbacks).toEqual([]);

    store.destroy();
  });

  it("leaves a restarted run of a known definition to the device event's re-read", async () => {
    const store = makeStore();
    store.trackerDefinitions = [{ id: "site", name: "Infusion Site" }];

    store.websocketClient.eventHandlers.trackerUpdate?.({
      action: TrackerRealtimeAction.Create,
      instance: { id: "run", definitionId: "site", definitionName: "Infusion Site" },
    });
    await vi.advanceTimersByTimeAsync(0);

    expect(api.getLegacyFallbacks).not.toHaveBeenCalled();

    store.destroy();
  });

  it("re-reads the trackers and fallbacks on a reconnection, not on the first connect", async () => {
    const store = makeStore();
    api.emptyPage.mockResolvedValue({ data: [] });
    api.getCurrentTherapyState.mockResolvedValue({ reservoir: null });
    api.apsGetAll.mockResolvedValue({ data: [] });
    const info: ConnectionInfo = { clientId: "socket", serverTime: "", version: "" };

    store.websocketClient.eventHandlers.connect?.(info);
    await vi.advanceTimersByTimeAsync(0);
    expect(api.getTrackerDefinitions).not.toHaveBeenCalled();

    api.getTrackerDefinitions.mockResolvedValue([{ id: "site", name: "Site" }]);
    api.getLegacyFallbacks.mockResolvedValue(read([fallback(LegacyConsumableKind.Sensor, 3)]));
    store.websocketClient.eventHandlers.connect?.(info);
    await vi.advanceTimersByTimeAsync(0);

    expect(store.trackerDefinitions.map((d) => d.id)).toEqual(["site"]);
    expect(store.legacyFallbacks.map((f) => f.kind)).toEqual([LegacyConsumableKind.Sensor]);

    store.destroy();
  });

  it("does not re-read the trackers on a backfill alone", async () => {
    const store = makeStore();
    api.emptyPage.mockResolvedValue({ data: [] });
    api.getCurrentTherapyState.mockResolvedValue({ reservoir: null });
    api.apsGetAll.mockResolvedValue({ data: [] });

    await store.performBackfillIfNeeded(true);

    expect(api.getTrackerDefinitions).not.toHaveBeenCalled();
    expect(api.getLegacyFallbacks).not.toHaveBeenCalled();

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

describe("RealtimeStore backfill cursor", () => {
  const NOW = Date.parse("2026-10-20T12:00:00Z");
  const MINUTE = 60_000;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    api.getCurrentTherapyState.mockResolvedValue({ reservoir: null });
    api.apsGetAll.mockResolvedValue({ data: [] });
    api.emptyPage.mockResolvedValue({ data: [] });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  /** A store whose cursor and liveness clock sit `minutes` in the past, then 1 minute passes. */
  function idleStore(minutes: number): TestStore {
    const store = makeStore();
    store.backfillCursor = NOW - minutes * MINUTE;
    store.lastEventReceived = NOW - minutes * MINUTE;
    vi.setSystemTime(NOW + MINUTE);
    return store;
  }

  /** The `from` the first range query of the last backfill was given. */
  const queriedFrom = () => Date.parse(api.emptyPage.mock.calls[0][0]);

  it.each([
    ["legacy treatments create", { colName: "treatments", doc: { _id: "t1", mills: NOW } }],
    ["unheld tempBasal create", { colName: "care", recordType: "tempBasal", doc: { id: "tb", mills: NOW } }],
    ["unheld sensorGlucose create", { colName: "glucose", recordType: "sensorGlucose", doc: { id: "s", mills: NOW } }],
  ] as [string, StorageEvent][])("keeps the cursor still but the store live on a %s", (_name, event) => {
    const store = idleStore(10);
    const cursor = store.backfillCursor;

    store.handleCreate(event);

    expect(store.backfillCursor).toBe(cursor);
    expect(store.lastEventReceived).toBe(Date.now());
    store.destroy();
  });

  it("moves the cursor when a bolus is merged", () => {
    const store = idleStore(10);

    store.handleCreate({ colName: "care", recordType: "bolus", doc: { id: "b1", insulin: 1, mills: NOW } });

    expect(store.backfillCursor).toBe(Date.now());
    store.destroy();
  });

  it("keeps the cursor still for a bolus outside the store's window", () => {
    const store = idleStore(10);
    const cursor = store.backfillCursor;

    store.handleCreate({ colName: "care", recordType: "bolus", doc: { id: "old", insulin: 1, mills: NOW - 48 * 60 * MINUTE } });

    expect(store.backfillCursor).toBe(cursor);
    store.destroy();
  });

  it("moves the cursor when a device status is merged, and not for one already held", () => {
    const store = idleStore(10);
    const status = { colName: "devicestatus", doc: { _id: "d1", mills: NOW, pump: {} } };

    store.handleCreate(status);
    const merged = store.backfillCursor;
    vi.setSystemTime(NOW + 5 * MINUTE);
    store.handleCreate(status);

    expect(merged).toBe(NOW + MINUTE);
    expect(store.backfillCursor).toBe(merged);
    store.destroy();
  });

  it("moves the cursor when a live data update adds a reading", () => {
    const store = idleStore(10);

    store.handleDataUpdate({ data: [{ _id: "e1", type: "sgv", sgv: 100, mills: NOW }] });

    expect(store.backfillCursor).toBe(Date.now());
    store.destroy();
  });

  it("skips an unforced backfill after an event that merged nothing, since the store is live", async () => {
    const store = idleStore(10);

    store.handleCreate({ colName: "treatments", doc: { _id: "t1", mills: NOW } });
    await store.performBackfillIfNeeded();

    expect(api.emptyPage).not.toHaveBeenCalled();
    store.destroy();
  });

  it("fetches a treatment stamped just before an unrelated event", async () => {
    const store = idleStore(30);
    store.entries = [{ _id: "e0", type: "sgv", sgv: 100, mills: NOW - 30 * MINUTE }];
    const eventAt = Date.now();
    store.handleCreate({ colName: "treatments", doc: { _id: "t1", mills: eventAt } });
    const missed = { id: "b-missed", mills: eventAt - 5_000 };
    api.emptyPage.mockImplementation(async (from: string, _to: string, limit: number) => ({
      data: limit === 500 && missed.mills >= Date.parse(from) ? [missed] : [],
    }));

    await store.performBackfillIfNeeded(true);

    expect(store.boluses.map((bolus) => bolus.id)).toEqual(["b-missed"]);
    expect(queriedFrom()).toBeLessThan(missed.mills);
    store.destroy();
  });

  it("starts a margin before the cursor", async () => {
    const store = idleStore(10);
    store.entries = [{ _id: "e0", type: "sgv", sgv: 100, mills: NOW }];

    await store.performBackfillIfNeeded(true);

    expect(queriedFrom()).toBe(NOW - 10 * MINUTE - 2 * MINUTE);
    store.destroy();
  });

  it("starts from the newest held record when it is older than the cursor", async () => {
    const store = idleStore(10);
    store.notes = [{ id: "n1", mills: NOW - 40 * MINUTE }] as typeof store.notes;
    store.boluses = [{ id: "b1", mills: NOW - 20 * MINUTE }] as typeof store.boluses;

    await store.performBackfillIfNeeded(true);

    expect(queriedFrom()).toBe(NOW - 20 * MINUTE - 2 * MINUTE);
    store.destroy();
  });

  it("backfills an empty store from the 24 hour floor", async () => {
    const store = idleStore(10);

    await store.performBackfillIfNeeded(true);

    expect(queriedFrom()).toBe(Date.now() - 24 * 60 * MINUTE);
    store.destroy();
  });

  it("moves the cursor when an update merges an unheld bolus", () => {
    const store = idleStore(10);

    store.handleUpdate({ colName: "care", recordType: "bolus", doc: { id: "b1", insulin: 1, mills: NOW } });

    expect(store.boluses.map((bolus) => bolus.id)).toEqual(["b1"]);
    expect(store.backfillCursor).toBe(Date.now());
    store.destroy();
  });

  it("moves the cursor when an apsSnapshot is created", () => {
    const store = idleStore(10);

    store.handleCreate({ colName: "device", recordType: "apsSnapshot", doc: { id: "a1", iob: 1, mills: NOW } });

    expect(store.apsSnapshots).toHaveLength(1);
    expect(store.backfillCursor).toBe(Date.now());
    store.destroy();
  });

  it("moves the cursor when the batched entry creates flush", () => {
    const store = idleStore(10);
    const cursor = store.backfillCursor;

    store.handleCreate({ colName: "entries", doc: { _id: "e1", type: "sgv", sgv: 100, mills: NOW } });
    expect(store.backfillCursor).toBe(cursor);
    vi.advanceTimersByTime(1_000);

    expect(store.entries).toHaveLength(1);
    expect(store.backfillCursor).toBeGreaterThan(cursor);
    store.destroy();
  });

  it("never starts before the 24 hour floor", async () => {
    const store = idleStore(60 * 48);

    await store.performBackfillIfNeeded(true);

    expect(queriedFrom()).toBe(Date.now() - 24 * 60 * MINUTE);
    store.destroy();
  });

  it("sets the cursor to the instant the backfill began, not to when it ended", async () => {
    const store = idleStore(10);
    const began = Date.now();
    api.emptyPage.mockImplementation(async () => {
      vi.setSystemTime(began + 30_000);
      return { data: [] };
    });

    await store.performBackfillIfNeeded(true);

    expect(store.backfillCursor).toBe(began);
    store.destroy();
  });
});

describe("RealtimeStore starting reads", () => {
  const reading = { id: "sg-1", mills: 1_000, mgdl: 120 };
  const snapshot = { id: "aps-1", mills: 1_000 };
  const record = { id: "rec-1", mills: 1_000 };
  const thresholds = { low: 70, high: 180 };

  /** A store with a socket URL, so `initialize()` runs the starting reads, and a stubbed connect. */
  function connectingStore(): {
    store: TestStore;
    connect: ReturnType<typeof vi.fn>;
    prefetchTicket: ReturnType<typeof vi.fn>;
  } {
    const store = makeStore("http://localhost");
    const connect = vi.fn();
    const prefetchTicket = vi.fn();
    store.websocketClient.connect = connect;
    store.websocketClient.prefetchTicket = prefetchTicket;
    return { store, connect, prefetchTicket };
  }

  function deferred<T>(): { promise: Promise<T>; resolve(value: T): void; reject(error: unknown): void } {
    let resolve!: (value: T) => void;
    let reject!: (error: unknown) => void;
    const promise = new Promise<T>((res, rej) => {
      resolve = res;
      reject = rej;
    });
    return { promise, resolve, reject };
  }

  /** Runs pending microtasks without firing any timer. */
  async function drainMicrotasks(): Promise<void> {
    for (let i = 0; i < 50; i++) await Promise.resolve();
  }

  beforeEach(() => {
    vi.useFakeTimers();
    api.getCurrentTherapyState.mockResolvedValue({ reservoir: 42, thresholds });
    api.apsGetAll.mockResolvedValue({ data: [snapshot] });
    api.emptyList.mockResolvedValue([]);
    // The glucose read asks for up to 1000 readings; the day's other record reads for 500.
    api.emptyPage.mockImplementation((_from: unknown, _to: unknown, limit: number) =>
      Promise.resolve({ data: limit === 1000 ? [reading] : limit === 500 ? [record] : [] })
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
    vi.clearAllMocks();
    api.emptyPage.mockReset();
  });

  it("requests the socket ticket while the reads are in flight", async () => {
    const therapy = deferred<null>();
    api.getCurrentTherapyState.mockReturnValue(therapy.promise);
    api.getProfileSummary.mockResolvedValue(null);
    const { store, prefetchTicket } = connectingStore();
    let readSettled = false;
    therapy.promise.then(() => (readSettled = true));

    const initialized = store.initialize();

    expect(prefetchTicket).toHaveBeenCalledTimes(1);
    expect(readSettled).toBe(false);

    therapy.resolve(null);
    await vi.advanceTimersByTimeAsync(0);
    await initialized;
    store.destroy();
  });

  it("does not request a socket ticket without a socket URL", async () => {
    const store = makeStore();
    const prefetchTicket = vi.fn();
    store.websocketClient.prefetchTicket = prefetchTicket;

    await store.initialize();

    expect(prefetchTicket).not.toHaveBeenCalled();
    store.destroy();
  });

  it("applies the current reading while the other reads are still pending", async () => {
    const profile = deferred<null>();
    api.getProfileSummary.mockReturnValue(profile.promise);
    const { store, connect } = connectingStore();

    void store.initialize();
    await vi.advanceTimersByTimeAsync(0);

    expect(store.currentReadingLoaded).toBe(true);
    expect(store.currentEntry?.sgv).toBe(120);
    expect(store.apsSnapshots).toEqual([snapshot]);
    expect(store.currentReservoir).toBe(42);
    expect(store.chartThresholds).toEqual(thresholds);
    expect(store.isReady).toBe(false);
    expect(store.boluses).toEqual([]);
    expect(connect).not.toHaveBeenCalled();

    profile.resolve(null);
    await vi.advanceTimersByTimeAsync(0);
    store.destroy();
  });

  it("applies the other reads in a later task, then marks the store ready and connects", async () => {
    const profile = deferred<{ units: string } | null>();
    api.getProfileSummary.mockReturnValue(profile.promise);
    const { store, connect } = connectingStore();

    const initialized = store.initialize();
    await vi.advanceTimersByTimeAsync(0);
    profile.resolve({ units: "mg/dL" });
    await drainMicrotasks();

    expect(store.boluses).toEqual([]);
    expect(store.isReady).toBe(false);
    expect(connect).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(0);
    await initialized;

    expect(store.boluses).toEqual([record]);
    expect(store.profile).toEqual({ units: "mg/dL" });
    expect(store.isReady).toBe(true);
    expect(connect).toHaveBeenCalledTimes(1);
    store.destroy();
  });

  it("applies the current reading in an earlier task than the rest when everything lands at once", async () => {
    api.getProfileSummary.mockResolvedValue(null);
    const { store, connect } = connectingStore();

    const initialized = store.initialize();
    await drainMicrotasks();

    expect(store.currentReadingLoaded).toBe(true);
    expect(store.isReady).toBe(false);
    expect(store.boluses).toEqual([]);

    await vi.advanceTimersByTimeAsync(0);
    await initialized;

    expect(store.isReady).toBe(true);
    expect(store.boluses).toEqual([record]);
    expect(connect).toHaveBeenCalledTimes(1);
    store.destroy();
  });

  /** A document in the given visibility, with `requestAnimationFrame` held until `frame()` runs it. */
  function onDocument(visibilityState: "visible" | "hidden"): { frame(): void } {
    const frames: FrameRequestCallback[] = [];
    vi.stubGlobal("document", { visibilityState, addEventListener: vi.fn(), removeEventListener: vi.fn() });
    vi.stubGlobal("window", { addEventListener: vi.fn(), removeEventListener: vi.fn() });
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => frames.push(callback));
    vi.stubGlobal("cancelAnimationFrame", vi.fn());
    return { frame: () => frames.splice(0).forEach((callback) => callback(0)) };
  }

  it("waits for a frame before applying the other reads in a visible document", async () => {
    api.getProfileSummary.mockResolvedValue(null);
    const { frame } = onDocument("visible");
    const { store, connect } = connectingStore();

    const initialized = store.initialize();
    await vi.advanceTimersByTimeAsync(0);

    expect(store.currentReadingLoaded).toBe(true);
    expect(store.isReady).toBe(false);
    expect(connect).not.toHaveBeenCalled();

    frame();
    await vi.advanceTimersByTimeAsync(0);
    await initialized;

    expect(store.isReady).toBe(true);
    expect(store.boluses).toEqual([record]);
    expect(connect).toHaveBeenCalledTimes(1);
    store.destroy();
  });

  it("does not wait for a frame in a hidden document", async () => {
    api.getProfileSummary.mockResolvedValue(null);
    onDocument("hidden");
    const { store, connect } = connectingStore();

    const initialized = store.initialize();
    await vi.advanceTimersByTimeAsync(0);

    expect(store.isReady).toBe(true);
    expect(connect).toHaveBeenCalledTimes(1);
    await initialized;
    store.destroy();
  });

  it("is ready once the other reads settle even when one of them fails", async () => {
    api.getProfileSummary.mockRejectedValue(new Error("offline"));
    api.emptyList.mockRejectedValue(new Error("offline"));
    const { store, connect } = connectingStore();

    const initialized = store.initialize();
    await vi.advanceTimersByTimeAsync(0);
    await initialized;

    expect(store.isReady).toBe(true);
    expect(store.currentReadingLoaded).toBe(true);
    expect(store.profile).toBeNull();
    expect(store.boluses).toEqual([record]);
    expect(connect).toHaveBeenCalledTimes(1);
    store.destroy();
  });
});

describe("loadInitialGlucose", () => {
  const FROM = "2026-10-04T00:00:00.000Z";

  function clientWith(getAll: ReturnType<typeof vi.fn>) {
    return { getAll } as unknown as Parameters<typeof loadInitialGlucose>[0];
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

  it("covers the sidebar sparkline's whole window, which draws from the store alone", async () => {
    const NOW = Date.parse("2026-10-10T12:00:00.000Z");
    const { glucoseFrom } = initialReadWindows(NOW);
    const FIVE_MINUTES = 5 * 60_000;
    const window = Array.from({ length: GLUCOSE_WINDOW_HOURS * 12 + 1 }, (_, i) => ({
      id: `r${i}`,
      mills: NOW - i * FIVE_MINUTES,
      mgdl: 100 + i,
    }));
    const getAll = vi.fn(async (from?: string) => ({
      data: window.filter((r) => r.mills >= Date.parse(from ?? "0")),
    }));

    const entries = await loadInitialGlucose(clientWith(getAll as never), glucoseFrom);

    expect(Math.min(...entries.map((e) => e.mills ?? 0))).toBe(NOW - GLUCOSE_WINDOW_HOURS * 3_600_000);
    expect(entries).toHaveLength(window.length);
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

  it("falls back to the newest readings when the window is empty, so a tenant with only old readings has a current one", async () => {
    const getAll = vi.fn().mockResolvedValueOnce({ data: [] }).mockResolvedValueOnce(readings(140));

    const entries = await loadInitialGlucose(clientWith(getAll), FROM);

    expect(entries.map((e) => e.sgv)).toEqual([140]);
  });

  it("returns no readings when the request fails", async () => {
    const getAll = vi.fn().mockRejectedValue(new Error("offline"));

    await expect(loadInitialGlucose(clientWith(getAll), FROM)).resolves.toEqual([]);
  });
});
