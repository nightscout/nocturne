import { beforeEach, describe, it, expect, vi } from "vitest";
import { resolveChartThresholds } from "#lib/constants/glucose-thresholds.js";

const context = vi.hoisted(() => ({ store: null as unknown }));

vi.mock("svelte", async (importOriginal) => ({
  ...(await importOriginal<typeof import("svelte")>()),
  getContext: () => context.store,
}));

import { tenantChartThresholds } from "./realtime-store.svelte";

describe("tenantChartThresholds", () => {
  beforeEach(() => {
    context.store = null;
  });

  it("falls back when there is no realtime store", () => {
    expect(tenantChartThresholds().current).toEqual(resolveChartThresholds());
  });

  it("falls back for a store that has not loaded thresholds", () => {
    context.store = { chartThresholds: null };

    expect(tenantChartThresholds().current).toEqual(resolveChartThresholds());
  });

  it("falls back for the all-zero thresholds of a tenant with no profile", () => {
    context.store = {
      chartThresholds: { veryLow: 0, low: 0, high: 0, veryHigh: 0, glucoseYMax: 0 },
    };

    expect(tenantChartThresholds().current).toEqual(resolveChartThresholds());
  });

  it("follows the store as the tenant's thresholds load", () => {
    const store = { chartThresholds: null as unknown };
    context.store = store;
    const thresholds = tenantChartThresholds();

    store.chartThresholds = {
      veryLow: 60,
      low: 80,
      high: 160,
      veryHigh: 220,
      glucoseYMax: 300,
    };

    expect(thresholds.current).toMatchObject({
      veryLow: 60,
      low: 80,
      high: 160,
      veryHigh: 220,
      glucoseYMax: 300,
    });
  });
});
