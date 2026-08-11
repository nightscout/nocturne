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
import type { StorageEvent } from "$lib/websocket/types";

/** The realtime/backfill entry points, which the class keeps private. */
interface StoreInternals {
  handleCreate(event: StorageEvent): void;
  performBackfillIfNeeded(force?: boolean): Promise<void>;
}

type TestStore = StoreInternals &
  Pick<RealtimeStore, "currentReservoir" | "destroy">;

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
