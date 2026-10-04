import { afterEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { userEvent } from "vitest/browser";
import { timeDay } from "d3-time";
import type { DailySummaryDay } from "$api/generated/nocturne-api-client";
import { PRINT_LAYOUT_CLASS } from "$lib/components/charts/print/print-mode.svelte";
import Harness from "./YearHeatmap.test-harness.svelte";

const START_OF_2024 = new Date(2024, 0, 1);
const FEB_29 = new Date(2024, 1, 29);
const DEC_31 = new Date(2024, 11, 31);

const yearData = new Map<number, DailySummaryDay[]>([
  [
    2024,
    [
      {
        date: "2024-02-29",
        averageGlucoseMgdl: 123,
        totalBolusUnits: 5.5,
        totalBasalUnits: 22.3,
        totalDailyDose: 27.8,
        totalCarbs: 45,
        timeInRangePercent: 70,
        totalCount: 292,
        counts: { Glucose: 288, Boluses: 4 },
      },
      {
        date: "2024-12-31",
        averageGlucoseMgdl: 140,
        totalBolusUnits: 6,
        totalBasalUnits: 20,
        totalDailyDose: 26,
        totalCarbs: 60,
        timeInRangePercent: 75,
        totalCount: 300,
        counts: { Glucose: 288 },
      },
    ],
  ],
]);

function dayCells(container: HTMLElement): SVGRectElement[] {
  return Array.from(
    container.querySelectorAll<SVGRectElement>('svg rect[rx="4"]')
  ).filter((rect) => rect.getAttribute("fill") !== "none");
}

async function waitForCells(container: HTMLElement, count: number) {
  await vi.waitFor(() => expect(dayCells(container)).toHaveLength(count));
  return dayCells(container);
}

function click(cell: SVGRectElement) {
  cell.dispatchEvent(new MouseEvent("click", { bubbles: true }));
}

async function waitForTooltip(): Promise<string> {
  await vi.waitFor(() =>
    expect(document.querySelector(".lc-tooltip-content")).not.toBeNull()
  );
  const content = document.querySelector(".lc-tooltip-content");
  if (!content) throw new Error("Tooltip content is not in the document");
  return content.textContent ?? "";
}

afterEach(() => {
  document.documentElement.classList.remove(PRINT_LAYOUT_CLASS);
});

describe("YearHeatmap", () => {
  it("lays out one cell per day of a leap year, including the leap day", async () => {
    const { container } = render(Harness, { yearData });

    const cells = await waitForCells(container, 366);

    expect(cells[timeDay.count(START_OF_2024, FEB_29)]).toBeDefined();
    expect(cells[timeDay.count(START_OF_2024, DEC_31)]).toBeDefined();
  });

  it("navigates the leap day and 31 December cells to their dates", async () => {
    const navigated: string[] = [];
    const { container } = render(Harness, {
      yearData,
      onNavigate: (date: string) => navigated.push(date),
    });

    const cells = await waitForCells(container, 366);
    await userEvent.click(cells[timeDay.count(START_OF_2024, FEB_29)]);
    await userEvent.click(cells[timeDay.count(START_OF_2024, DEC_31)]);

    expect(navigated).toEqual(["2024-02-29", "2024-12-31"]);
  });

  it("shows the complete day summary when the leap day is hovered", async () => {
    const { container } = render(Harness, { yearData });
    const cells = await waitForCells(container, 366);

    await userEvent.hover(cells[timeDay.count(START_OF_2024, FEB_29)]);
    const text = await waitForTooltip();

    expect(text).toContain("123");
    expect(text).toContain("mg/dL");
    expect(text).toContain("Bolus");
    expect(text).toContain("5.5 U");
    expect(text).toContain("Basal");
    expect(text).toContain("22.3 U");
    expect(text).toContain("TDD");
    expect(text).toContain("27.8 U");
    expect(text).toContain("Carbs");
    expect(text).toContain("45g");
    expect(text).toContain("Glucose");
    expect(text).toContain("288");
  });

  it("links every month and week to its report route", async () => {
    const { container } = render(Harness, { yearData });
    await waitForCells(container, 366);

    const hrefs = Array.from(container.querySelectorAll("a[href]")).map(
      (link) => link.getAttribute("href") ?? ""
    );

    const months = hrefs.filter((href) => href.startsWith("/calendar?"));
    expect(months).toHaveLength(12);
    expect(months).toContain("/calendar?year=2024&month=1");
    expect(months).toContain("/calendar?year=2024&month=12");

    const weeks = hrefs.filter((href) =>
      href.startsWith("/reports/week-to-week?")
    );
    expect(weeks.length).toBeGreaterThan(0);
    for (const href of weeks) {
      expect(href).toMatch(
        /^\/reports\/week-to-week\?from=\d{4}-\d{2}-\d{2}&to=\d{4}-\d{2}-\d{2}&isDefault=false$/
      );
    }
  });

  it("keeps 31 December in print layout and exposes its complete data", async () => {
    const navigated: string[] = [];
    const { container } = render(Harness, {
      yearData,
      onNavigate: (date: string) => navigated.push(date),
    });
    await waitForCells(container, 366);

    document.documentElement.classList.add(PRINT_LAYOUT_CLASS);
    const printCells = await waitForCells(container, 335);

    const printStart = new Date(2024, 1, 1);
    const lastCell = printCells.at(-1);
    if (!lastCell) throw new Error("Print layout dropped every cell");
    expect(lastCell).toBe(printCells[timeDay.count(printStart, DEC_31)]);

    click(lastCell);
    expect(navigated).toEqual(["2024-12-31"]);

    await userEvent.hover(lastCell);
    const text = await waitForTooltip();
    expect(text).toContain("140");
    expect(text).toContain("6.0 U");
    expect(text).toContain("20.0 U");
    expect(text).toContain("26.0 U");
    expect(text).toContain("60g");
    expect(text).toContain("Glucose");
  });

  it("renders offscreen days for print even without a viewport intersection", async () => {
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        observe() {}
        disconnect() {}
      }
    );
    try {
      const { container } = render(Harness, { yearData, yearIndex: 1 });
      expect(dayCells(container)).toHaveLength(0);
      document.documentElement.classList.add(PRINT_LAYOUT_CLASS);
      await waitForCells(container, 335);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
