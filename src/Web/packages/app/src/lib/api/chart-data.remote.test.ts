import { describe, expect, it, vi } from "vitest";

const { getDashboardChartData } = vi.hoisted(() => ({
  getDashboardChartData: vi.fn(async (_input: Record<string, unknown>) => ({})),
}));

vi.mock("$app/server", () => ({
  query: (_schema: unknown, fn: unknown) => fn,
}));
vi.mock("$api/generated/chartDatas.generated.remote", () => ({ getDashboardChartData }));

const { getChartData } = await import("./chart-data.remote");

describe("getChartData", () => {
  it("never asks for the heart-rate and step series the chart does not draw", async () => {
    await getChartData({ startTime: 0, endTime: 1, intervalMinutes: 5 });

    expect(getDashboardChartData).toHaveBeenCalledWith(
      expect.objectContaining({ includeHealthSeries: false })
    );
  });
});
