import { describe, expect, it, vi } from "vitest";
import { load } from "./+page.server";

type LoadEvent = Parameters<typeof load>[0];

function runLoad(getDashboardChartData: ReturnType<typeof vi.fn>) {
  return load({
    locals: { apiClient: { chartData: { getDashboardChartData } } },
    request: new Request("https://sleepy.nocturne.run/"),
    parent: async () => ({ tenantless: false, baseDomain: "nocturne.run", dashboardSlugs: [] }),
  } as unknown as LoadEvent) as Promise<{ streamed: { historicalChartData: Promise<unknown> } }>;
}

describe("dashboard page load", () => {
  it("asks for both chart windows without the heart-rate and step series", async () => {
    const getDashboardChartData = vi.fn().mockResolvedValue({});

    const data = await runLoad(getDashboardChartData);
    await data.streamed.historicalChartData;

    expect(getDashboardChartData).toHaveBeenCalledTimes(2);
    for (const call of getDashboardChartData.mock.calls) expect(call[3]).toBe(false);
  });

  it("starts the historical window before the initial window resolves", async () => {
    const releases: ((value: unknown) => void)[] = [];
    const getDashboardChartData = vi.fn(() => new Promise((resolve) => releases.push(resolve)));

    const pending = runLoad(getDashboardChartData);
    await vi.waitFor(() => expect(getDashboardChartData).toHaveBeenCalledTimes(2), { timeout: 500 });
    for (const release of releases) release({});
    await pending;
  });
});
