import { describe, it, expect, vi, beforeEach } from "vitest";

const getGlucose = vi.fn();
const getClockAlerts = vi.fn();
const getClockFaceData = vi.fn();
const getClockFaceChart = vi.fn();
vi.mock("#lib/api/browser-clients.js", () => ({
  clockFacesClient: () => ({ getGlucose }),
  clockFaceAlertsClient: () => ({ getClockAlerts }),
  clockFaceDataClient: () => ({ getClockFaceData, getClockFaceChart }),
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
  getClockFaceData.mockReset();
  getClockFaceChart.mockReset();
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

  it("reads no values and no chart for a face with neither", () => {
    const store = new PublicClockStore("clock-id");
    store.watchFace({ rows: [{ elements: [{ type: "sg" }, { type: "time" }] }] });
    store.stop();

    expect(getClockFaceData).not.toHaveBeenCalled();
    expect(getClockFaceChart).not.toHaveBeenCalled();
    expect(store.data).toBeNull();
    expect(store.chart).toBeNull();
  });

  it("reads the face's values and chart by its id", async () => {
    getClockFaceData.mockResolvedValue({ iob: 1.5, trackers: [] });
    getClockFaceChart.mockResolvedValue({
      chart: { glucoseData: [{ time: MILLS, sgv: 120 }] },
      predictions: null,
    });
    const store = new PublicClockStore("clock-id");

    store.watchFace({ rows: [{ elements: [{ type: "iob" }, { type: "chart" }] }] });
    await vi.waitFor(() => expect(store.chart).not.toBeNull());
    store.stop();

    expect(getClockFaceData).toHaveBeenCalledWith("clock-id");
    expect(getClockFaceChart).toHaveBeenCalledWith("clock-id");
    expect(store.data).toEqual({ iob: 1.5, trackers: [] });
    expect(store.chart?.glucoseData.map((p) => p.sgv)).toEqual([120]);
    expect(store.predictions).toBeNull();
  });

  it("keeps the last values when a poll fails", async () => {
    getClockFaceData
      .mockResolvedValueOnce({ cob: 12, trackers: [] })
      .mockRejectedValueOnce(new Error("offline"));
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.useFakeTimers();
    const store = new PublicClockStore("clock-id");

    store.watchFace({ rows: [{ elements: [{ type: "cob" }] }] });
    await vi.waitFor(() => expect(store.data).toEqual({ cob: 12, trackers: [] }));
    await vi.advanceTimersByTimeAsync(60_000);

    expect(getClockFaceData).toHaveBeenCalledTimes(2);
    expect(store.data).toEqual({ cob: 12, trackers: [] });
    store.stop();
    vi.useRealTimers();
  });

  it("drops the values once the polls have failed for three intervals", async () => {
    getClockFaceData
      .mockResolvedValueOnce({ iob: 2.3, trackers: [] })
      .mockRejectedValue(new Error("offline"));
    getClockFaceChart
      .mockResolvedValueOnce({ chart: { glucoseData: [{ time: MILLS, sgv: 120 }] }, predictions: null })
      .mockRejectedValue(new Error("offline"));
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.useFakeTimers();
    const store = new PublicClockStore("clock-id");

    store.watchFace({ rows: [{ elements: [{ type: "iob" }, { type: "chart" }] }] });
    await vi.waitFor(() => expect(store.chart).not.toBeNull());
    await vi.advanceTimersByTimeAsync(2 * 60_000);
    expect(store.data).toEqual({ iob: 2.3, trackers: [] });

    await vi.advanceTimersByTimeAsync(60_000);
    expect(store.data).toBeNull();
    expect(store.chart).not.toBeNull();

    await vi.advanceTimersByTimeAsync(12 * 60_000);
    expect(store.chart).toBeNull();
    store.stop();
    vi.useRealTimers();
  });
});
