import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { flushSync } from "svelte";

interface QueryState {
  loading: boolean;
  error: unknown;
  current: unknown;
  refresh: ReturnType<typeof vi.fn>;
}

const queries = vi.hoisted(() => ({
  count: undefined as unknown as QueryState,
  analysis: undefined as unknown as QueryState,
  epoch: { read: (): number => 0 },
}));

vi.mock("#lib/api/reports.remote.js", () => ({
  getReadingCount: () => queries.count,
  getReportsAnalysis: () => queries.analysis,
}));
vi.mock("#lib/hooks/date-params.svelte.js", () => ({
  requireDateParamsContext: () => ({
    get dateRangeInput() {
      return { epoch: queries.epoch.read() };
    },
    startDate: new Date(2026, 2, 1),
    endDate: new Date(2026, 2, 14),
    dayCount: 14,
  }),
}));

import Harness from "./ExecutiveSummaryGuardHarness.test.svelte";

const pending = (): QueryState => ({
  loading: true,
  error: null,
  current: undefined,
  refresh: vi.fn(),
});

const counted = (): QueryState => ({
  loading: false,
  error: null,
  current: {
    readingCount: 4321,
    dateRange: { from: "2026-03-01", to: "2026-03-14" },
  },
  refresh: vi.fn(),
});

const analysed = (): QueryState => ({
  loading: false,
  error: null,
  current: {
    analysis: {
      basicStats: { mean: 154 },
      glycemicVariability: {},
      timeInRange: { percentages: { target: 70, low: 3, veryLow: 1 } },
    },
    averagedStats: [],
    dateRange: {
      from: "2026-03-01",
      to: "2026-03-14",
      lastUpdated: "2026-03-14T00:00:00Z",
    },
  },
  refresh: vi.fn(),
});

const failed = (message: string): QueryState => ({
  loading: false,
  error: new Error(message),
  current: undefined,
  refresh: vi.fn(),
});

const skeletons = (container: HTMLElement) =>
  container.querySelectorAll('[data-slot="skeleton"]');

describe("executive summary loading", () => {
  beforeEach(() => {
    queries.count = pending();
    queries.analysis = pending();
    queries.epoch.read = () => 0;
  });

  it("keeps the skeleton up while the count has landed and the analysis has not", async () => {
    queries.count = counted();
    const { container } = render(Harness);

    await expect.poll(() => skeletons(container).length).toBeGreaterThan(0);
    await expect
      .element(
        page.getByText("Percentage of time in the consensus target range", {
          exact: false,
        })
      )
      .not.toBeInTheDocument();
  });

  it("keeps the skeleton up while the analysis has landed and the count has not", async () => {
    queries.analysis = analysed();
    const { container } = render(Harness);

    await expect.poll(() => skeletons(container).length).toBeGreaterThan(0);
    await expect
      .element(
        page.getByText("Percentage of time in the consensus target range", {
          exact: false,
        })
      )
      .not.toBeInTheDocument();
  });

  it("shows the report once both have landed", async () => {
    queries.count = counted();
    queries.analysis = analysed();
    const { container } = render(Harness);

    await expect
      .element(
        page.getByText("Percentage of time in the consensus target range", {
          exact: false,
        })
      )
      .toBeVisible();
    expect(skeletons(container)).toHaveLength(0);
  });

  it("shows the error of whichever query failed and retries both", async () => {
    queries.count = counted();
    queries.analysis = failed("The analytics service is down");
    render(Harness);

    await expect
      .element(page.getByText("The analytics service is down"))
      .toBeVisible();

    await page.getByRole("button", { name: "Try again" }).click();
    expect(queries.analysis.refresh).toHaveBeenCalledOnce();
    expect(queries.count.refresh).toHaveBeenCalledOnce();
  });

  it("shows the error when only the count failed and retries both", async () => {
    queries.analysis = analysed();
    queries.count = failed("The reading count is unavailable");
    render(Harness);

    await expect
      .element(page.getByText("The reading count is unavailable"))
      .toBeVisible();

    await page.getByRole("button", { name: "Try again" }).click();
    expect(queries.analysis.refresh).toHaveBeenCalledOnce();
    expect(queries.count.refresh).toHaveBeenCalledOnce();
  });

  it("keeps the report on screen with a status line while a new range loads", async () => {
    let epoch = $state(0);
    queries.epoch.read = () => epoch;
    queries.count = counted();
    queries.analysis = analysed();
    render(Harness);
    await expect
      .element(
        page.getByText("Percentage of time in the consensus target range", {
          exact: false,
        })
      )
      .toBeVisible();

    queries.count = pending();
    queries.analysis = pending();
    epoch += 1;
    flushSync();

    await expect
      .element(page.getByText("Loading the selected range"))
      .toBeVisible();
    await expect
      .element(
        page.getByText("Percentage of time in the consensus target range", {
          exact: false,
        })
      )
      .toBeVisible();
  });
});
