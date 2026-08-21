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

import { RealtimeStore } from "./realtime-store.svelte";
import type { StorageEvent, SyncProgressEvent } from "$lib/websocket/types";

/** The realtime/backfill entry points, which the class keeps private. */
interface StoreInternals {
  handleCreate(event: StorageEvent): void;
  performBackfillIfNeeded(force?: boolean): Promise<void>;
  websocketClient: {
    eventHandlers: { syncProgress?: (event: SyncProgressEvent) => void };
  };
}

type TestStore = StoreInternals &
  Pick<
    RealtimeStore,
    "currentReservoir" | "entries" | "direction" | "syncProgressByConnector" | "destroy"
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
    store.entries = [{ mills: 1_000, sgv: 120, direction: "FortyFiveDown" }];

    expect(store.direction).toBe("FortyFiveDown");

    store.destroy();
  });

  it.each([
    ["an entry with no direction", [{ mills: 1_000, sgv: 120 }]],
    ["an empty direction", [{ mills: 1_000, sgv: 120, direction: "" }]],
    ["no entries at all", []],
  ])("reports no direction for %s rather than Flat", (_case, entries) => {
    const store = makeStore();
    store.entries = entries;

    expect(store.direction).toBe("");

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
      emit(store, syncEvent("glooko", phase, phase === "Completed" ? "SyncComplete" : "SyncFailed"));

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
