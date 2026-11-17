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

vi.mock("$app/state", () => ({
  page: {
    data: {
      effectivePermissions: ["glucose.read", "reports.read"],
      user: { id: "owner" },
    },
  },
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
vi.mock("#lib/components/reports/TIRStackedChart.svelte", async () => ({
  default: (await import("#lib/components/reports/LineChartStub.test.svelte"))
    .default,
}));
vi.mock("#lib/components/ambulatory-glucose-profile/index.js", async () => ({
  AmbulatoryGlucoseProfile: (
    await import("#lib/components/reports/LineChartStub.test.svelte")
  ).default,
}));

import ReportsOverviewPage from "./+page.svelte";

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
      timeInRange: { percentages: { target: 70 } },
      basicStats: { mean: 154 },
      glycemicVariability: {},
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

describe("reports overview cards", () => {
  beforeEach(() => {
    queries.count = pending();
    queries.analysis = pending();
    queries.epoch.read = () => 0;
  });

  it("shows the reading count while the analysis card still loads", async () => {
    queries.count = counted();
    render(ReportsOverviewPage);

    await expect.element(page.getByText(/4,321\s+readings/)).toBeVisible();
    await expect
      .element(page.getByRole("status", { name: "Loading the range analysis" }))
      .toBeVisible();
    await expect
      .element(page.getByRole("heading", { name: "Time in range" }))
      .not.toBeInTheDocument();
  });

  it("shows the analysis cards while the reading count still loads", async () => {
    queries.analysis = analysed();
    render(ReportsOverviewPage);

    await expect
      .element(page.getByRole("heading", { name: "Time in range" }))
      .toBeVisible();
    await expect
      .element(page.getByRole("status", { name: "Loading the reading count" }))
      .toBeVisible();
    await expect
      .element(page.getByRole("status", { name: "Loading the range analysis" }))
      .not.toBeInTheDocument();
  });

  it("puts the analysis card in error and retries only that card, while the count shows", async () => {
    queries.count = counted();
    queries.analysis = failed("The analytics service is down");
    render(ReportsOverviewPage);

    await expect
      .element(page.getByText("The analytics service is down"))
      .toBeVisible();
    await expect.element(page.getByText(/4,321\s+readings/)).toBeVisible();
    await expect
      .element(
        page.getByRole("heading", { level: 1, name: "Reports", exact: true })
      )
      .toBeVisible();
    expect(page.getByRole("heading", { level: 1 }).elements()).toHaveLength(1);

    await page
      .getByRole("button", { name: "Try again to load the analysis" })
      .click();
    expect(queries.analysis.refresh).toHaveBeenCalledOnce();
    expect(queries.count.refresh).not.toHaveBeenCalled();
  });

  it("puts the count in error and retries only the count, while the analysis cards show", async () => {
    queries.count = failed("The count service is down");
    queries.analysis = analysed();
    render(ReportsOverviewPage);

    await expect
      .element(page.getByText("Could not load the reading count."))
      .toBeVisible();
    await expect
      .element(page.getByRole("heading", { name: "Time in range" }))
      .toBeVisible();
    expect(page.getByRole("heading", { level: 1 }).elements()).toHaveLength(1);

    await page
      .getByRole("button", { name: "Try again to load the reading count" })
      .click();
    expect(queries.count.refresh).toHaveBeenCalledOnce();
    expect(queries.analysis.refresh).not.toHaveBeenCalled();
  });

  it("names what each retry button retries when the count and the analysis both fail", async () => {
    queries.count = failed("The count service is down");
    queries.analysis = failed("The analytics service is down");
    render(ReportsOverviewPage);

    await expect
      .element(page.getByText("The analytics service is down"))
      .toBeVisible();

    await expect
      .element(
        page.getByRole("button", {
          name: "Try again to load the reading count",
          exact: true,
        })
      )
      .toBeVisible();
    await expect
      .element(
        page.getByRole("button", {
          name: "Try again to load the analysis",
          exact: true,
        })
      )
      .toBeVisible();
    expect(
      page.getByRole("button", { name: "Try again" }).elements()
    ).toHaveLength(2);
  });

  it("keeps the analysis on screen with a status line while a new range loads", async () => {
    queries.count = counted();
    queries.analysis = { ...analysed(), loading: true };
    render(ReportsOverviewPage);

    await expect
      .element(page.getByRole("heading", { name: "Time in range" }))
      .toBeVisible();
    await expect
      .element(page.getByText("Loading the selected range"))
      .toBeVisible();
    await expect
      .element(page.getByRole("status", { name: "Loading the range analysis" }))
      .not.toBeInTheDocument();
  });

  it("keeps the previous analysis on screen when a new range swaps in a pending query", async () => {
    let epoch = $state(0);
    queries.epoch.read = () => epoch;
    queries.count = counted();
    queries.analysis = analysed();
    render(ReportsOverviewPage);
    await expect
      .element(page.getByRole("heading", { name: "Time in range" }))
      .toBeVisible();

    queries.analysis = pending();
    epoch += 1;
    flushSync();

    await expect
      .element(page.getByRole("heading", { name: "Time in range" }))
      .toBeVisible();
    await expect
      .element(page.getByText("Loading the selected range"))
      .toBeVisible();
    await expect
      .element(page.getByRole("status", { name: "Loading the range analysis" }))
      .not.toBeInTheDocument();
  });

  it("keeps the previous reading count on screen when a new range swaps in a pending query", async () => {
    let epoch = $state(0);
    queries.epoch.read = () => epoch;
    queries.count = counted();
    queries.analysis = analysed();
    render(ReportsOverviewPage);
    await expect.element(page.getByText(/4,321\s+readings/)).toBeVisible();

    queries.count = pending();
    epoch += 1;
    flushSync();

    await expect.element(page.getByText(/4,321\s+readings/)).toBeVisible();
    await expect
      .element(page.getByRole("status", { name: "Loading the reading count" }))
      .not.toBeInTheDocument();
  });
});
