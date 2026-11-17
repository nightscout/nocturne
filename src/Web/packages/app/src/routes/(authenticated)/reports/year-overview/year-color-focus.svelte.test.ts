import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import type { DailySummaryDay } from "#lib/api/generated/nocturne-api-client.js";
import { yearOverviewMocks } from "#lib/test-stubs/year-overview-remote.js";
import { glucoseUnits, yearOverviewColors, applyPreferences, collectPreferences, registerPreferencesWriteThrough, parsePrefsCookie } from "#lib/stores/appearance-store.svelte.js";
import { getGlucoseHeatmapFill } from "#lib/utils/chart-colors.js";
import { rampColor, rampPosition, GLUCOSE_LIMITS, GLUCOSE_RAMP } from "#lib/utils/metric-color-focus.js";

vi.mock("#lib/api/generated/dataOverviews.generated.remote.js", async () =>
  import("#lib/test-stubs/year-overview-remote.js")
);
vi.mock("#lib/hooks/date-params.svelte.js", async () =>
  import("#lib/test-stubs/year-overview-runtime.svelte.js")
);
vi.mock(
  "#lib/components/reports/year-overview/YearHeatmap.svelte",
  async () => import("#lib/test-stubs/YearHeatmap.test-stub.svelte")
);
vi.mock(
  "#lib/components/reports/year-overview/YearOverviewFilters.svelte",
  async () => import("#lib/test-stubs/EmptyYearPanel.test-stub.svelte")
);
vi.mock(
  "#lib/components/reports/GlycemicRiskIndexChart.svelte",
  async () => import("#lib/test-stubs/EmptyYearPanel.test-stub.svelte")
);

import YearOverviewPage from "./+page.svelte";

const storageKey = () => "nocturne-year-overview-colors";
const stored = () => JSON.parse(window.localStorage.getItem(storageKey())!);
const minimum = (metric = "TDD") =>
  page.getByRole("spinbutton", { name: `${metric} focus minimum` });
const maximum = (metric = "TDD") =>
  page.getByRole("spinbutton", { name: `${metric} focus maximum` });
const boundary = (n: number, metric = "Avg glucose") =>
  page.getByRole("spinbutton", { name: `${metric} color boundary ${n}`, exact: true });
const addBoundary = () => page.getByRole("button", { name: "Color boundary", exact: true });
const reset = (metric = "TDD") =>
  page.getByRole("button", { name: `Reset ${metric} color scale` });
const cell = (day: string) => page.getByTestId(`cell-${day}`);
const observedYears = new Map<number, () => void>();
/** The insulin ramp `100 - loShare` percent of the way across the window. */
const insulinAt = (loShare: string) =>
  `color-mix(in srgb, color-mix(in srgb, var(--insulin) 15%, transparent) ${loShare}%, var(--insulin))`;

function day(date: string, dose: number | null): DailySummaryDay {
  return {
    date,
    averageGlucoseMgdl: 120,
    totalCount: 1,
    counts: { Glucose: 1 },
    totalDailyDose: dose ?? undefined,
    totalBolusUnits: dose ?? undefined,
    timeInRangePercent: 75,
  };
}

async function selectMetric(name: string) {
  await page
    .getByRole("button", {
      name: /^(Avg glucose|Time in range|Bolus|Basal|TDD|Carbs)$/,
    })
    .click();
  await page.getByRole("option", { name, exact: true }).click();
}

async function setWindow(min: number, max: number, metric = "TDD") {
  await minimum(metric).fill(String(min));
  await maximum(metric).fill(String(max));
}

describe("year overview page color focus integration", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    window.localStorage.clear();
    applyPreferences({ yearOverviewColors: {} });
    glucoseUnits.current = "mg/dl";
    observedYears.clear();
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        private observed = new Map<number, () => void>();
        constructor(private callback: IntersectionObserverCallback) {}
        observe(target: HTMLElement) {
          const year = Number(target.dataset.year);
          const intersect = () => {
            this.callback(
              [{ target, isIntersecting: true } as unknown as IntersectionObserverEntry],
              this as unknown as IntersectionObserver
            );
          };
          this.observed.set(year, intersect);
          observedYears.set(year, intersect);
        }
        disconnect() {
          for (const [year, callback] of this.observed) {
            if (observedYears.get(year) === callback)
              observedYears.delete(year);
          }
          this.observed.clear();
        }
      }
    );
    yearOverviewMocks.years.mockResolvedValue({
      years: [2026],
      availableDataSources: [],
    });
    yearOverviewMocks.days.mockResolvedValue({
      days: [
        day("2026-01-01", 20),
        day("2026-01-02", 40),
        day("2026-01-03", 60),
        day("2026-01-04", 500),
      ],
    });
    yearOverviewMocks.gri.mockResolvedValue({ periods: [] });
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it("colors each metric in its own theme color, with no palette or advanced mode", async () => {
    render(YearOverviewPage);
    await expect.element(cell("2026-01-01")).toHaveTextContent(getGlucoseHeatmapFill(120));
    await selectMetric("TDD");
    await expect.element(cell("2026-01-04")).toHaveTextContent(/^var\(--insulin\)$/);
    await selectMetric("Carbs");
    await expect.element(page.getByRole("button", { name: /palette/i })).not.toBeInTheDocument();
    await expect.element(page.getByRole("checkbox", { name: "Advanced" })).not.toBeInTheDocument();
    expect(page.getByRole("slider").elements()).toHaveLength(2);
  });

  it("remembers independent metric windows across switches and a page remount", async () => {
    const screen = render(YearOverviewPage);
    await expect.element(cell("2026-01-04")).toBeInTheDocument();
    await selectMetric("TDD");
    await setWindow(10, 70);
    await expect.element(cell("2026-01-02")).toHaveTextContent(insulinAt("50.00"));

    await selectMetric("Bolus");
    await setWindow(2, 25, "Bolus");
    await selectMetric("TDD");
    await expect.element(minimum()).toHaveValue(10);
    await expect.element(maximum()).toHaveValue(70);
    expect(stored()).toEqual({ tddBand: [10, 70], bolusBand: [2, 25] });

    await screen.unmount();
    render(YearOverviewPage);
    await selectMetric("Bolus");
    await expect.element(minimum("Bolus")).toHaveValue(2);
    await expect.element(maximum("Bolus")).toHaveValue(25);
    await reset("Bolus").click();
    expect(stored()).toEqual({ tddBand: [10, 70] });
  });

  it("drops keys saved in the retired shape on the next change", async () => {
    applyPreferences({
      yearOverviewColors: {
        advancedMode: true,
        avgGlucose: [54, 72, 180, 250],
        tddColors: ["#000", "#fff"],
        tddBand: [5, 60],
      } as never,
    });
    render(YearOverviewPage);
    await selectMetric("TDD");
    await expect.element(minimum()).toHaveValue(5);
    await maximum().fill("70");
    expect(stored()).toEqual({ tddBand: [5, 70] });
  });

  it("leaves off the bands and dimming a retired blob saved with advanced mode off", async () => {
    applyPreferences({
      yearOverviewColors: {
        advancedMode: false,
        tddBand: [5, 10],
        outOfBandTransparency: 40,
      } as never,
    });
    render(YearOverviewPage);
    await selectMetric("TDD");
    await expect.element(minimum()).toHaveValue(0);
    await expect.element(maximum()).toHaveValue(500);
    await expect.element(page.getByRole("spinbutton", { name: "Dim outside" })).toHaveValue(90);
    await maximum().fill("70");
    expect(stored()).toEqual({ tddBand: [0, 70] });
  });

  it("keeps a saved window when lazy loading introduces a larger outlier", async () => {
    applyPreferences({ yearOverviewColors: { tddBand: [10, 70] } });
    yearOverviewMocks.years.mockResolvedValue({
      years: [2026, 2025],
      availableDataSources: [],
    });
    const olderYear = Promise.withResolvers<{ days: DailySummaryDay[] }>();
    yearOverviewMocks.days.mockImplementation(({ year }) =>
      year === 2026
        ? Promise.resolve({
            days: [day("2026-01-01", 40), day("2026-01-02", 70)],
          })
        : olderYear.promise
    );
    render(YearOverviewPage);
    await expect.element(cell("2026-01-01")).toBeInTheDocument();
    await selectMetric("TDD");
    await expect.element(cell("2026-01-01")).toHaveTextContent(insulinAt("50.00"));
    await vi.waitFor(() => expect(observedYears.has(2025)).toBe(true));
    observedYears.get(2025)!();
    await vi.waitFor(() =>
      expect(yearOverviewMocks.days).toHaveBeenCalledWith({ year: 2025 })
    );
    olderYear.resolve({ days: [day("2025-01-01", 1000)] });
    await expect
      .element(cell("2025-01-01"))
      .toHaveTextContent("color-mix(in srgb, var(--insulin) 10%, transparent)");
    await expect.element(cell("2026-01-01")).toHaveTextContent(insulinAt("50.00"));
    await expect.element(minimum()).toHaveValue(10);
    await expect.element(maximum()).toHaveValue(70);
    await expect
      .element(page.getByRole("slider", { name: "TDD focus maximum" }))
      .toHaveAttribute("aria-valuemax", "1000");

    await reset().click();
    await expect.element(maximum()).toHaveValue(1000);
    await expect.element(cell("2026-01-01")).toHaveTextContent(insulinAt("96.00"));
  });

  it("dims values outside the window without treating missing readings as zero", async () => {
    yearOverviewMocks.days.mockResolvedValue({
      days: [
        day("2026-01-01", null),
        day("2026-01-02", 0),
        day("2026-01-03", 10),
        day("2026-01-04", 70),
        day("2026-01-05", 500),
      ],
    });
    render(YearOverviewPage);
    await selectMetric("TDD");
    await setWindow(10, 70);
    await expect.element(cell("2026-01-01")).toHaveTextContent("var(--muted)");
    await expect
      .element(cell("2026-01-02"))
      .toHaveTextContent(
        "color-mix(in srgb, color-mix(in srgb, var(--insulin) 15%, transparent) 10%, transparent)"
      );
    await expect
      .element(cell("2026-01-03"))
      .toHaveTextContent(/^color-mix\(in srgb, var\(--insulin\) 15%, transparent\)$/);
    await expect.element(cell("2026-01-04")).toHaveTextContent(/^var\(--insulin\)$/);
    await expect
      .element(cell("2026-01-05"))
      .toHaveTextContent("color-mix(in srgb, var(--insulin) 10%, transparent)");

    await page.getByRole("spinbutton", { name: "Dim outside" }).fill("50");
    await expect
      .element(cell("2026-01-05"))
      .toHaveTextContent("color-mix(in srgb, var(--insulin) 50%, transparent)");
  });

  it("syncs to the backend and cookie even when browser storage cannot save", async () => {
    const save = vi.fn();
    registerPreferencesWriteThrough(save);
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new DOMException("Blocked", "SecurityError");
    });
    render(YearOverviewPage);
    await expect.element(cell("2026-01-02")).toBeInTheDocument();
    await selectMetric("TDD");
    await setWindow(10, 70);
    await vi.waitFor(() => expect(save).toHaveBeenCalledWith(expect.objectContaining({ yearOverviewColors: { tddBand: [10, 70] } })));
    const cookie = document.cookie.split("; ").find(row => row.startsWith("nocturne-prefs="))!.slice("nocturne-prefs=".length);
    expect(parsePrefsCookie(cookie)?.yearOverviewColors).toEqual({ tddBand: [10, 70] });
    await expect.element(cell("2026-01-02")).toHaveTextContent(insulinAt("50.00"));
  });

  it("keeps glucose colors fixed to the reading while its window only dims", async () => {
    render(YearOverviewPage);
    await minimum("Avg glucose").fill("130");
    expect(stored()).toEqual({ avgGlucoseBand: [130, 350] });
    await expect
      .element(cell("2026-01-01"))
      .toHaveTextContent(`color-mix(in srgb, ${getGlucoseHeatmapFill(120)} 10%, transparent)`);
    await reset("Avg glucose").click();
    await expect.element(cell("2026-01-01")).toHaveTextContent(getGlucoseHeatmapFill(120));
  });

  it("adds a glucose boundary that leaves the colors alone until it moves, across units and remounts", async () => {
    const screen = render(YearOverviewPage);
    await addBoundary().click();
    await expect.element(boundary(1)).toHaveValue(195);
    const [pin] = stored().avgGlucoseBoundaries;
    expect(pin.value).toBe(195);
    await expect.element(cell("2026-01-01")).toHaveTextContent(getGlucoseHeatmapFill(120));

    await boundary(1).fill("150");
    const moved = [{ value: 150, position: pin.position }];
    await expect
      .element(cell("2026-01-01"))
      .toHaveTextContent(rampColor(GLUCOSE_RAMP, rampPosition(120, GLUCOSE_LIMITS, moved)));
    expect(rampColor(GLUCOSE_RAMP, rampPosition(120, GLUCOSE_LIMITS, moved))).not.toBe(
      getGlucoseHeatmapFill(120)
    );

    glucoseUnits.current = "mmol";
    await expect.element(boundary(1)).toHaveValue(8.3);
    expect(stored().avgGlucoseBoundaries[0].value).toBe(150);
    glucoseUnits.current = "mg/dl";
    await screen.unmount();
    render(YearOverviewPage);
    await expect.element(boundary(1)).toHaveValue(150);
    await page.getByRole("button", { name: "Remove Avg glucose color boundary 1" }).click();
    expect(stored()).toEqual({});
    await expect.element(cell("2026-01-01")).toHaveTextContent(getGlucoseHeatmapFill(120));
  });

  it("fits the panel on a phone and drags the glucose window", async () => {
    const originalSize = [window.innerWidth, window.innerHeight] as const;
    await page.viewport(390, 800);
    try {
      render(YearOverviewPage);
      const track = page.getByTestId("glucose-color-track");
      const bounds = (track.element() as HTMLElement).getBoundingClientRect();
      expect(bounds.width).toBeGreaterThan(300);
      await userEvent.dragAndDrop(
        page.getByRole("slider", { name: "Avg glucose focus maximum" }),
        track,
        { targetPosition: { x: bounds.width * 0.5, y: bounds.height / 2 } }
      );
      const value = (maximum("Avg glucose").element() as HTMLInputElement).valueAsNumber;
      expect(value).toBeGreaterThan(190);
      expect(value).toBeLessThan(200);
      expect(
        (page.getByTestId("glucose-color-focus").element() as HTMLElement)
          .getBoundingClientRect().right
      ).toBeLessThanOrEqual(390);
      await page.screenshot({ path: "test-results/bg-color-focus-mobile.png" });
    } finally {
      await page.viewport(originalSize[0], originalSize[1]);
    }
  });

  it("hydrates server preferences and restores automatic windows after a saved reset", async () => {
    applyPreferences({ yearOverviewColors: { tddBand: [10, 70] } });
    render(YearOverviewPage);
    await selectMetric("TDD");
    await expect.element(minimum()).toHaveValue(10);
    await reset().click();
    const saved = collectPreferences();
    applyPreferences({ yearOverviewColors: { tddBand: [20, 60] } });
    applyPreferences(saved);
    await expect.element(maximum()).toHaveValue(500);
    expect(yearOverviewColors.current).toEqual({});
  });

  it("collapses to the metric and its scale, and stays collapsed after a remount", async () => {
    const screen = render(YearOverviewPage);
    await expect.element(cell("2026-01-04")).toBeInTheDocument();
    await page.getByRole("button", { name: "Collapse Avg Glucose color scale" }).click();
    expect(page.getByRole("slider").elements()).toHaveLength(0);
    await expect.element(page.getByTestId("collapsed-scale")).toBeInTheDocument();
    expect(page.getByTestId("collapsed-scale").element().getAttribute("style")).toContain("linear-gradient");

    await selectMetric("TDD");
    await expect.element(cell("2026-01-04")).toHaveTextContent(/^var\(--insulin\)$/);

    await screen.unmount();
    render(YearOverviewPage);
    await expect.element(page.getByRole("button", { name: "Expand Avg Glucose color scale" })).toBeInTheDocument();
    await page.getByRole("button", { name: "Expand Avg Glucose color scale" }).click();
    expect(page.getByRole("slider").elements()).toHaveLength(2);
  });

  it("saves a picked end color under the metric's own key", async () => {
    render(YearOverviewPage);
    await selectMetric("TDD");
    const input = page.getByLabelText("TDD high end color", { exact: true }).element();
    if (!(input instanceof HTMLInputElement)) throw new TypeError("Expected a color input");
    input.value = "#123456";
    input.dispatchEvent(new Event("input", { bubbles: true }));
    await expect.element(cell("2026-01-04")).toHaveTextContent("#123456");
    expect(stored()).toEqual({ tddHighColor: "#123456" });
    await reset().click();
    expect(stored()).toEqual({});
  });
});
