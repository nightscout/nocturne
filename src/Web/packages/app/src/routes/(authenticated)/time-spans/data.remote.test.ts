import { describe, it, expect, vi } from "vitest";
import { BasalDeliveryOrigin, StateSpanCategory } from "$lib/api";

const day = new Date(2026, 0, 10, 8).getTime();
const hour = 3_600_000;

const stateSpans = {
  getPumpModes: vi.fn(async () => ({ data: [] })),
  getOverrides: vi.fn(async () => ({ data: [] })),
  getActivities: vi.fn(async () => ({ data: [] })),
  getStateSpans: vi.fn(async () => ({
    data: [
      { id: "ex", category: StateSpanCategory.Exercise, state: "Run", startMills: day, endMills: day + hour },
      { id: "mo", category: StateSpanCategory.Motion, state: "Walking", startMills: day, endMills: day + hour },
    ],
  })),
};
const chartData = {
  getBasalDeliverySpans: vi.fn(async () => [
    { id: "tb0", startMills: day - hour, endMills: day + hour, rate: 0.8, origin: BasalDeliveryOrigin.Inferred, fillColor: "insulin-basal" },
    { id: "tb1", startMills: day, endMills: day + hour, rate: 1.4, origin: BasalDeliveryOrigin.Algorithm, source: "loop", fillColor: "insulin-basal" },
    { id: "tb2", startMills: day + hour, endMills: day + 2 * hour, rate: 0, origin: BasalDeliveryOrigin.Suspended, fillColor: "insulin-basal" },
  ]),
};

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({
    locals: {
      apiClient: {
        stateSpans,
        chartData,
        profileActivations: { getActivations: async () => [] },
      },
    },
  }),
  query: (schemaOrFn: unknown, fn?: unknown) => fn ?? schemaOrFn,
}));

const { getTimeSpansData } = await import("./data.remote");

describe("getTimeSpansData temp-basal lane", () => {
  it("draws only the backend basal delivery spans, never a state span of any category", async () => {
    const data = await (getTimeSpansData as unknown as (i: { from: string; to: string }) => Promise<Awaited<ReturnType<typeof getTimeSpansData>>>)({
      from: "2026-01-10",
      to: "2026-01-10",
    });

    expect(stateSpans.getStateSpans).not.toHaveBeenCalled();
    expect(chartData.getBasalDeliverySpans).toHaveBeenCalledWith(
      new Date(2026, 0, 10, 0, 0, 0).getTime(),
      new Date(2026, 0, 10, 23, 59, 59).getTime()
    );
    expect(data.tempBasalSpans.map((s) => [s.id, s.rate, s.origin])).toEqual([
      ["tb0", 0.8, BasalDeliveryOrigin.Inferred],
      ["tb1", 1.4, BasalDeliveryOrigin.Algorithm],
      ["tb2", 0, BasalDeliveryOrigin.Suspended],
    ]);
  });
});
