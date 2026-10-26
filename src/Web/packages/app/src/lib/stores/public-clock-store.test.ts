import { describe, it, expect, vi, beforeEach } from "vitest";

const getGlucose = vi.fn();
const getClockAlerts = vi.fn();
vi.mock("#lib/api/browser-clients.js", () => ({
  clockFacesClient: () => ({ getGlucose }),
  clockFaceAlertsClient: () => ({ getClockAlerts }),
}));

const { PublicClockStore } = await import("./public-clock-store.svelte");

const MILLS = Date.UTC(2026, 11, 31, 14, 5);

async function storeOf(readings: unknown[]) {
  getGlucose.mockResolvedValue(readings);
  const store = new PublicClockStore("clock-id");
  await store.start();
  store.stop();
  return store;
}

beforeEach(() => {
  vi.stubGlobal("window", globalThis);
  getGlucose.mockReset();
  getClockAlerts.mockReset();
});

describe("PublicClockStore", () => {
  it("has no reading, age or delta before any reading arrives", async () => {
    const store = await storeOf([]);
    expect(store.currentBG).toBeNull();
    expect(store.lastUpdated).toBeNull();
    expect(store.bgDelta).toBeNull();
  });

  it("claims no change from a lone reading", async () => {
    const store = await storeOf([{ mgdl: 120, mills: MILLS }]);
    expect(store.currentBG).toBe(120);
    expect(store.lastUpdated).toBe(MILLS);
    expect(store.bgDelta).toBeNull();
  });

  it("measures the change against the previous reading", async () => {
    const store = await storeOf([
      { mgdl: 120, mills: MILLS },
      { mgdl: 113, mills: MILLS - 5 * 60_000 },
    ]);
    expect(store.bgDelta).toBe(7);
  });

  it("prefers the delta the reading carried", async () => {
    const store = await storeOf([
      { mgdl: 120, mills: MILLS, delta: 4 },
      { mgdl: 113, mills: MILLS - 5 * 60_000 },
    ]);
    expect(store.bgDelta).toBe(4);
  });

  it("asks for no alerts from a face that does not show them", () => {
    const store = new PublicClockStore("clock-id");
    store.watchAlerts(false);
    store.stop();
    expect(getClockAlerts).not.toHaveBeenCalled();
    expect(store.alerts).toEqual([]);
  });

  it("keeps the last alerts when a poll fails", async () => {
    const alert = { id: "a", ruleName: "Low", severity: "critical" };
    getClockAlerts.mockResolvedValueOnce([alert]).mockRejectedValueOnce(new Error("offline"));
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.useFakeTimers();
    const store = new PublicClockStore("clock-id");

    store.watchAlerts(true);
    await vi.waitFor(() => expect(store.alerts).toEqual([alert]));
    await vi.advanceTimersByTimeAsync(10_000);

    expect(getClockAlerts).toHaveBeenCalledTimes(2);
    expect(store.alerts).toEqual([alert]);
    store.stop();
    vi.useRealTimers();
  });
});
