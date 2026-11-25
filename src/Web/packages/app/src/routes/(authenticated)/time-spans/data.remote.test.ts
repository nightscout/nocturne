import { describe, it, expect, vi } from "vitest";
import { DeviceEventType, StateSpanCategory } from "#lib/api/index.js";

const from = new Date(2026, 0, 10).getTime();
const hour = 3_600_000;
const to = from + 24 * hour;

const refused = async () => {
  throw Object.assign(new Error("forbidden"), { status: 403 });
};

const apiClient = {
  profileHistory: {
    getProfileHistory: vi.fn(refused),
    getProfileLanes: vi.fn(refused),
    getGlucoseTrace: vi.fn(async () => [{ mills: from + hour, average: 120, min: 100, max: 140 }]),
    getGlucoseAroundChange: vi.fn(refused),
  },
  chartData: { getBasalDeliverySpans: vi.fn(async () => []) },
  stateSpans: {
    getPumpModes: vi.fn(async () => ({
      data: [{ id: "pm", category: StateSpanCategory.PumpMode, state: "Automatic", startMills: from - hour }],
    })),
    getOverrides: vi.fn(async () => ({ data: [] })),
    getActivities: vi.fn(async () => ({ data: [] })),
  },
  sleep: { getSessions: vi.fn(async () => ({ data: [{ id: "s", startMills: from + hour, endMills: to + hour }] })) },
  deviceEvent: {
    getAll: vi.fn(async () => ({
      data: [
        { id: "site", mills: from + hour, eventType: DeviceEventType.SiteChange },
        { id: "prime", mills: from + 2 * hour, eventType: DeviceEventType.Priming },
      ],
    })),
  },
};

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({ locals: { apiClient } }),
  query: (schemaOrFn: unknown, fn?: unknown) => fn ?? schemaOrFn,
}));

const remote = await import("./data.remote");
type Window = { from: number; to: number };
const overview = remote.getTimelineOverview as unknown as (i: Window) => ReturnType<typeof remote.getTimelineOverview>;
const stage = remote.getTimelineStage as unknown as (i: Window) => ReturnType<typeof remote.getTimelineStage>;

describe("therapy timeline reads", () => {
  it("leaves out a refused category and keeps the others", async () => {
    const data = await overview({ from, to });

    expect(data.history).toBeNull();
    expect(data.glucose).toHaveLength(1);
  });

  it("clips spans to the window and runs an open one to its end", async () => {
    const data = await stage({ from, to });

    expect(data.lanes).toBeNull();
    expect(data.pumpModes).toEqual([
      expect.objectContaining({ id: "pm", label: "Automatic", startMills: from, endMills: to }),
    ]);
    expect(data.sleep).toEqual([expect.objectContaining({ startMills: from + hour, endMills: to })]);
  });

  it("draws only the device changes that start a new sensor, site, reservoir, pod or battery", async () => {
    const data = await stage({ from, to });

    expect(data.deviceEvents?.map((e) => e.id)).toEqual(["site"]);
  });
});
