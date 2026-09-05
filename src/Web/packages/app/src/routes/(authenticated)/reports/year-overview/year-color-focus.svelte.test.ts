import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import type { DailySummaryDay } from "$api/generated/nocturne-api-client";
import { yearOverviewMocks } from "$lib/test-stubs/year-overview-remote";
import {
  page as applicationPage,
  glucoseUnits,
} from "$lib/test-stubs/year-overview-runtime.svelte";
import { getGlucoseHeatmapFill } from "$lib/utils/chart-colors";
import { glucoseColorFocusStops } from "$lib/utils/metric-color-focus";
import YearOverviewPage from "./+page.svelte";

const storageKey = (user = "synthetic-user") =>
  `nocturne-year-color-focus-v1:${JSON.stringify(["synthetic-tenant", user])}`;
const minimum = (metric = "TDD") =>
  page.getByRole("spinbutton", { name: `${metric} minimum color value` });
const maximum = (metric = "TDD") =>
  page.getByRole("spinbutton", { name: `${metric} maximum color value` });
const cell = (day: string) => page.getByTestId(`cell-${day}`);
const observedYears = new Map<number, () => void>();
const bgInput = (name = "High") =>
  page.getByRole("spinbutton", {
    name: `Average glucose ${name} color boundary`,
    exact: true,
  });
const bgSlider = (name = "High") =>
  page.getByRole("slider", {
    name: `Average glucose ${name} color boundary`,
    exact: true,
  });

function day(date: string, dose: number | null): DailySummaryDay {
  return {
    date,
    averageGlucoseMgdl: 120,
    totalCount: 1,
    counts: { Glucose: 1 },
    totalDailyDose: dose,
    totalBolusUnits: dose,
    timeInRangePercent: 75,
  };
}

async function selectMetric(name: string) {
  await page
    .getByRole("button", {
      name: /^(Avg Glucose|Time in Range|Bolus|Basal|TDD|Carbs)$/,
    })
    .click();
  await page.getByRole("option", { name, exact: true }).click();
}

async function setRange(min: number, max: number, metric = "TDD") {
  await minimum(metric).fill(String(min));
  await maximum(metric).fill(String(max));
}

describe("year overview page color focus integration", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    window.localStorage.clear();
    applicationPage.data.user.subjectId = "synthetic-user";
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
              [{ target, isIntersecting: true } as IntersectionObserverEntry],
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

  it("remembers independent metric limits across switches and a page remount", async () => {
    const screen = render(YearOverviewPage);
    await expect.element(cell("2026-01-04")).toBeInTheDocument();
    await selectMetric("TDD");
    await setRange(10, 70);
    await expect
      .element(cell("2026-01-02"))
      .toHaveTextContent("var(--chart-4) 58%");

    await selectMetric("Bolus");
    await setRange(2, 25, "Bolus");
    await selectMetric("TDD");
    await expect.element(minimum()).toHaveValue(10);
    await expect.element(maximum()).toHaveValue(70);
    expect(JSON.parse(window.localStorage.getItem(storageKey())!)).toEqual({
      tdd: [10, 70],
      bolus: [2, 25],
    });

    await screen.unmount();
    render(YearOverviewPage);
    await selectMetric("TDD");
    await expect.element(minimum()).toHaveValue(10);
    await expect.element(maximum()).toHaveValue(70);
    await selectMetric("Bolus");
    await expect.element(minimum("Bolus")).toHaveValue(2);
    await expect.element(maximum("Bolus")).toHaveValue(25);
    await page
      .getByRole("button", { name: "Reset Bolus color range to automatic" })
      .click();
    expect(JSON.parse(window.localStorage.getItem(storageKey())!)).toEqual({
      tdd: [10, 70],
    });
  });

  it("keeps a saved manual focus when lazy loading introduces a larger outlier", async () => {
    window.localStorage.setItem(
      storageKey(),
      JSON.stringify({ tdd: [10, 70] })
    );
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
    await expect
      .element(cell("2026-01-01"))
      .toHaveTextContent("var(--chart-4) 58%");
    await vi.waitFor(() => expect(observedYears.has(2025)).toBe(true));
    observedYears.get(2025)!();
    await vi.waitFor(() =>
      expect(yearOverviewMocks.days).toHaveBeenCalledWith({ year: 2025 })
    );
    olderYear.resolve({ days: [day("2025-01-01", 1000)] });
    await expect
      .element(cell("2025-01-01"))
      .toHaveTextContent("var(--chart-4) 100%");
    await expect
      .element(cell("2026-01-01"))
      .toHaveTextContent("var(--chart-4) 58%");
    await expect.element(minimum()).toHaveValue(10);
    await expect.element(maximum()).toHaveValue(70);
    await expect
      .element(page.getByRole("slider", { name: "TDD maximum color value" }))
      .toHaveAttribute("aria-valuemax", "1000");

    await page
      .getByRole("button", { name: "Reset TDD color range to automatic" })
      .click();
    await expect.element(maximum()).toHaveValue(1000);
    await expect
      .element(cell("2026-01-01"))
      .toHaveTextContent("var(--chart-4) 18%");
  });

  it("saturates outliers without treating missing readings as zero or changing glucose colors", async () => {
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
    await expect
      .element(cell("2026-01-01"))
      .toHaveTextContent(getGlucoseHeatmapFill(120));
    expect(page.getByRole("slider").elements()).toHaveLength(4);
    await selectMetric("TDD");
    await setRange(10, 70);
    await expect.element(cell("2026-01-01")).toHaveTextContent("var(--muted)");
    await expect
      .element(cell("2026-01-02"))
      .toHaveTextContent("var(--chart-4) 15%");
    await expect
      .element(cell("2026-01-03"))
      .toHaveTextContent("var(--chart-4) 15%");
    await expect
      .element(cell("2026-01-04"))
      .toHaveTextContent("var(--chart-4) 100%");
    await expect
      .element(cell("2026-01-05"))
      .toHaveTextContent("var(--chart-4) 100%");

    await selectMetric("Avg Glucose");
    expect(page.getByRole("slider").elements()).toHaveLength(4);
    await expect
      .element(cell("2026-01-01"))
      .toHaveTextContent(getGlucoseHeatmapFill(120));
  });

  it("keeps the control usable and reports when browser storage cannot save", async () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new DOMException("Blocked", "SecurityError");
    });
    render(YearOverviewPage);
    await expect.element(cell("2026-01-02")).toBeInTheDocument();
    await selectMetric("TDD");
    await setRange(10, 70);
    await expect
      .element(page.getByRole("status"))
      .toHaveTextContent("This browser could not save the color settings");
    await expect
      .element(cell("2026-01-02"))
      .toHaveTextContent("var(--chart-4) 58%");
    await selectMetric("Bolus");
    await selectMetric("TDD");
    await expect.element(minimum()).toHaveValue(10);
    await expect.element(maximum()).toHaveValue(70);
  });

  it("keeps four BG color boundaries across metric, unit and page changes, then resets only BG", async () => {
    const screen = render(YearOverviewPage);
    await expect
      .element(cell("2026-01-01"))
      .toHaveTextContent(getGlucoseHeatmapFill(120));
    expect(page.getByRole("slider").elements()).toHaveLength(4);
    await bgInput("High").fill("140");
    const expected = [54, 70, 140, 250] as const;
    await expect
      .element(cell("2026-01-01"))
      .toHaveTextContent(
        getGlucoseHeatmapFill(120, glucoseColorFocusStops(expected))
      );
    const stored = JSON.parse(window.localStorage.getItem(storageKey())!);
    expect(stored.avgGlucose).toEqual(expected);

    glucoseUnits.current = "mmol";
    await expect.element(bgInput("High")).toHaveValue(7.8);
    expect(
      JSON.parse(window.localStorage.getItem(storageKey())!).avgGlucose
    ).toEqual(expected);
    glucoseUnits.current = "mg/dl";
    await selectMetric("TDD");
    await setRange(10, 70);
    await selectMetric("Avg Glucose");
    await expect.element(bgInput("High")).toHaveValue(140);
    await screen.unmount();
    render(YearOverviewPage);
    await expect.element(bgInput("High")).toHaveValue(140);
    await page
      .getByRole("button", { name: "Reset average glucose color boundaries" })
      .click();
    await expect.element(bgInput("High")).toHaveValue(180);
    expect(JSON.parse(window.localStorage.getItem(storageKey())!)).toEqual({
      tdd: [10, 70],
    });
    await expect
      .element(cell("2026-01-01"))
      .toHaveTextContent(getGlucoseHeatmapFill(120));
  });

  it("rejects crossing and empty BG inputs and moves a boundary with the keyboard", async () => {
    render(YearOverviewPage);
    await expect.element(bgInput("Low")).toHaveValue(70);
    for (const invalid of ["", "54", "200", "-1"]) {
      await bgInput("Low").fill(invalid);
      await expect
        .element(bgInput("Low"))
        .toHaveAttribute("aria-invalid", "true");
      expect(window.localStorage.getItem(storageKey())).toBeNull();
    }
    await bgInput("Low").fill("100");
    (bgSlider("Low").element() as HTMLElement).focus();
    await userEvent.keyboard("{ArrowRight}");
    await expect.element(bgInput("Low")).toHaveValue(101);
    await userEvent.keyboard("{End}");
    const saved = JSON.parse(
      window.localStorage.getItem(storageKey())!
    ).avgGlucose;
    expect(saved[1]).toBeLessThan(saved[2]);
  });

  it("edits BG in mmol and restores canonical mg/dL without changing other boundaries", async () => {
    glucoseUnits.current = "mmol";
    render(YearOverviewPage);
    await expect.element(bgInput("High")).toHaveValue(10);
    await bgInput("High").fill("8");
    expect(
      JSON.parse(window.localStorage.getItem(storageKey())!).avgGlucose
    ).toEqual([54, 70, 144, 250]);
    glucoseUnits.current = "mg/dl";
    await expect.element(bgInput("High")).toHaveValue(144);
  });

  it("drags a BG boundary on the mobile gradient without overflowing the page", async () => {
    const originalSize = [window.innerWidth, window.innerHeight] as const;
    await page.viewport(390, 800);
    try {
      const { container } = render(YearOverviewPage);
      await expect.element(bgInput("High")).toHaveValue(180);
      const track = container.querySelector<HTMLElement>("[data-glucose-color-track]")!;
      const bounds = track.getBoundingClientRect();
      expect(bounds.width).toBeGreaterThan(300);
      const originalGradient = track.style.background;
      await userEvent.dragAndDrop(bgSlider("High"), track, {
        targetPosition: { x: bounds.width * 0.6, y: bounds.height / 2 },
      });
      const value = (bgInput("High").element() as HTMLInputElement).valueAsNumber;
      expect(value).toBeGreaterThan(224);
      expect(value).toBeLessThan(228);
      await expect.element(bgInput("Very high")).toHaveValue(250);
      expect(track.style.background).not.toBe(originalGradient);
      expect(container.querySelector<HTMLElement>(".glucose-color-focus")!.getBoundingClientRect().right).toBeLessThanOrEqual(390);
      await page.screenshot({ path: "test-results/bg-color-focus-mobile.png" });
    } finally {
      await page.viewport(originalSize[0], originalSize[1]);
    }
  });

  it("does not restore another user's saved focus on the same browser", async () => {
    window.localStorage.setItem(
      storageKey(),
      JSON.stringify({ tdd: [10, 70] })
    );
    applicationPage.data.user.subjectId = "another-synthetic-user";
    render(YearOverviewPage);
    await expect.element(cell("2026-01-04")).toBeInTheDocument();
    await selectMetric("TDD");
    await expect.element(minimum()).toHaveValue(0);
    await expect.element(maximum()).toHaveValue(500);
    await setRange(20, 60);
    expect(
      JSON.parse(
        window.localStorage.getItem(storageKey("another-synthetic-user"))!
      )
    ).toEqual({ tdd: [20, 60] });
    expect(JSON.parse(window.localStorage.getItem(storageKey())!)).toEqual({
      tdd: [10, 70],
    });
  });
});
