import { beforeEach, afterEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import type {
  DailySummaryResponse,
  GriTimelineResponse,
} from "$api/generated/nocturne-api-client";

const requests = vi.hoisted(() => ({
  metadata: vi.fn(),
  daily: vi.fn(),
  gri: vi.fn(),
}));
vi.mock("$api/generated/dataOverviews.generated.remote", () => ({
  getAvailableYears: () => ({
    run: () => requests.metadata(),
  }),
  getYearSummary: (params: { year: number }) => ({
    run: async () => {
      const [dailySummary, griTimeline] = await Promise.all([
        requests.daily(params),
        requests.gri(params),
      ]);
      return { dailySummary, griTimeline };
    },
  }),
}));
vi.mock(
  "$lib/components/reports/year-overview/YearHeatmap.svelte",
  async () => ({
    default: (
      await import("$lib/components/reports/year-overview/YearHeatmap.test-stub.svelte")
    ).default,
  })
);
vi.mock(
  "$lib/components/reports/year-overview/HeatmapLegend.svelte",
  async () => ({
    default: (
      await import("$lib/components/reports/year-overview/Empty.test-stub.svelte")
    ).default,
  })
);
vi.mock("$lib/components/reports/GlycemicRiskIndexChart.svelte", async () => ({
  default: (
    await import("$lib/components/reports/year-overview/Empty.test-stub.svelte")
  ).default,
}));

import Harness from "./YearOverview.test-harness.svelte";

let intersect: IntersectionObserverCallback;
beforeEach(() => {
  requests.metadata.mockReset();
  requests.metadata.mockResolvedValue({
    years: [2023, 2024, 2025],
    availableDataSources: ["vendor-a"],
  });
  requests.daily.mockReset();
  requests.gri.mockReset();
  vi.stubGlobal(
    "IntersectionObserver",
    class {
      constructor(callback: IntersectionObserverCallback) {
        intersect = callback;
      }
      observe() {}
      disconnect() {}
    }
  );
});
afterEach(() => vi.unstubAllGlobals());

function enter(...years: number[]) {
  const entries = years.map((year) => ({
    isIntersecting: true,
    target: document.querySelector(`[data-year="${year}"]`)!,
  })) as IntersectionObserverEntry[];
  intersect(entries, {} as IntersectionObserver);
}

describe("year overview page loading", () => {
  it("waits for metadata when Print is clicked before discovery finishes", async () => {
    let discover!: (response: {
      years: number[];
      availableDataSources: string[];
    }) => void;
    requests.metadata.mockImplementation(
      () =>
        new Promise((resolve) => {
          discover = resolve;
        })
    );
    requests.daily.mockResolvedValue({ days: [] });
    requests.gri.mockResolvedValue({ periods: [] });
    render(Harness);
    await vi.waitFor(() => expect(requests.metadata).toHaveBeenCalledOnce());
    await page.getByRole("button", { name: "Prepare print" }).click();
    expect(requests.metadata).toHaveBeenCalledOnce();
    discover({ years: [2025], availableDataSources: [] });
    await expect
      .element(page.getByTestId("print-ready"))
      .toHaveTextContent("true");
    expect(requests.daily).toHaveBeenCalledOnce();
  });

  it("locks source filters until printing finishes its layout wait", async () => {
    let settle!: () => void;
    requests.daily.mockResolvedValue({ days: [] });
    requests.gri.mockResolvedValue({ periods: [] });
    render(Harness, {
      props: {
        settle: () =>
          new Promise<void>((resolve) => {
            settle = resolve;
          }),
      },
    });
    const sources = page.getByRole("button", { name: "All Data Sources" });
    await expect.element(sources).not.toBeDisabled();
    await page.getByRole("button", { name: "Prepare print" }).click();
    await vi.waitFor(() => expect(settle).toBeDefined());
    await expect.element(sources).toBeDisabled();
    settle();
    await expect
      .element(page.getByTestId("print-ready"))
      .toHaveTextContent("true");
    await expect.element(sources).not.toBeDisabled();
  });
  it("finishes the newest year before loading another visible year", async () => {
    let finishDaily!: (response: DailySummaryResponse) => void;
    let finishGri!: (response: GriTimelineResponse) => void;
    requests.daily.mockImplementation(
      () =>
        new Promise<DailySummaryResponse>((resolve) => {
          finishDaily = resolve;
        })
    );
    requests.gri.mockImplementation(
      () =>
        new Promise<GriTimelineResponse>((resolve) => {
          finishGri = resolve;
        })
    );
    render(Harness);
    await vi.waitFor(() => expect(requests.daily).toHaveBeenCalledOnce());
    expect(requests.gri).toHaveBeenCalledOnce();
    enter(2023, 2024, 2025);
    expect(requests.daily).toHaveBeenCalledOnce();
    finishDaily({
      days: [
        {
          date: "2025-01-01",
          totalCarbs: 45,
          totalBasalUnits: 18,
          totalDailyDose: 32,
        },
      ],
    });
    enter(2024, 2023);
    expect(requests.daily).toHaveBeenCalledOnce();
    finishGri({ periods: [] });
    await expect
      .element(page.getByTestId("year-2025"))
      .toHaveTextContent('"totalCarbs":45');
    await vi.waitFor(() =>
      expect(document.querySelector("[data-year='2024']")).not.toBeNull()
    );
    await new Promise((resolve) => setTimeout(resolve, 0));
    enter(2023, 2024);
    await vi.waitFor(() => expect(requests.daily).toHaveBeenCalledTimes(2));
    expect(requests.daily.mock.calls[1][0].year).toBe(2024);
    finishDaily({ days: [] });
    finishGri({ periods: [] });
  });

  it("loads deferred years before the report is ready to print", async () => {
    requests.daily.mockImplementation(async ({ year }: { year: number }) => ({
      days: [{ date: `${year}-01-01`, totalCarbs: 45 }],
    }));
    requests.gri.mockResolvedValue({ periods: [] });
    render(Harness);
    await expect
      .element(page.getByTestId("year-2025"))
      .toHaveTextContent("2025-01-01");
    expect(requests.daily).toHaveBeenCalledOnce();
    await page.getByRole("button", { name: "Prepare print" }).click();
    await expect
      .element(page.getByTestId("print-ready"))
      .toHaveTextContent("true");
    expect(requests.daily.mock.calls.map((call) => call[0].year)).toEqual([
      2025, 2024, 2023,
    ]);
    await expect
      .element(page.getByTestId("year-2023"))
      .toHaveTextContent('"totalCarbs":45');
  });
});
