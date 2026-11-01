import { render } from "vitest-browser-svelte";
import { carbRatioFormat, glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MetricComparison, PeriodComparison } from "#lib/api/generated/nocturne-api-client.js";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";
import { GlucoseUnit, PeriodComparisonMetric, TimeZoneUnavailableReason } from "#lib/api/index.js";

let served: PeriodComparison;
let requested: { units: GlucoseUnit }[];

vi.mock("#lib/api/reports.remote.js", () => ({
  getReportsComparison: (input: { units: GlucoseUnit }) => {
    requested.push(input);
    return remoteQuery(() => served);
  },
}));

vi.mock("runed/kit", () => ({
  useSearchParams: () => ({
    preset: "custom",
    aFrom: "2026-09-01",
    aTo: "2026-09-01",
    bFrom: "2026-09-02",
    bTo: "2026-09-02",
    aLabel: "A",
    bLabel: "B",
    update: vi.fn(),
  }),
}));

vi.mock("#lib/components/reports/TIRStackedChart.svelte", () =>
  import("#lib/test-stubs/Empty.test-stub.svelte")
);

import ComparisonReport from "./ComparisonReport.svelte";

function metric(key: PeriodComparisonMetric, figures: Partial<MetricComparison>): MetricComparison {
  return { metric: key, valueDecimals: 1, deltaDecimals: 1, ...figures };
}

function comparison(metrics: MetricComparison[], units = GlucoseUnit.MgDl): PeriodComparison {
  return {
    units,
    periodA: { hasData: true },
    periodB: { hasData: true },
    metrics,
  };
}

async function cells(label: string) {
  const locator = page.getByText(label, { exact: true });
  await expect.element(locator).toBeVisible();
  const row = locator.element().parentElement!;
  return Array.from(row.children).map((cell) => cell.textContent?.trim());
}

async function bar(label: string) {
  await cells(label);
  const row = page.getByText(label, { exact: true }).element().parentElement!;
  const fill = row.children[3].children[1] as HTMLElement;
  return {
    flat: fill.classList.contains("w-0.5"),
    side: fill.classList.contains("right-1/2") ? "down" : fill.classList.contains("left-1/2") ? "up" : "flat",
    width: fill.style.getPropertyValue("--bar-w"),
  };
}

describe("served figures", () => {
  beforeEach(() => {
    requested = [];
    served = comparison([
      metric(PeriodComparisonMetric.HypoMinutesPerDay, {
        valueA: 0,
        valueB: 30,
        delta: 30,
        valueDecimals: 0,
        deltaDecimals: 0,
      }),
      metric(PeriodComparisonMetric.HypoEventsPerDay, {
        valueA: 0.03,
        valueB: 0.04,
        delta: 0.01,
        percentChange: 33.3,
        valueDecimals: 2,
        deltaDecimals: 2,
      }),
      metric(PeriodComparisonMetric.HyperMinutesPerDay, {
        valueA: 60,
        valueB: 60,
        delta: 0,
        percentChange: 0,
        valueDecimals: 0,
        deltaDecimals: 0,
      }),
      metric(PeriodComparisonMetric.HyperEventsPerDay, {
        valueA: 2,
        valueB: 2,
        delta: 0,
        percentChange: 0,
        valueDecimals: 2,
        deltaDecimals: 2,
      }),
      metric(PeriodComparisonMetric.Gmi, {
        valueA: 7.1,
        valueB: 7.0,
        delta: -0.06,
        percentChange: -0.85,
        deltaDecimals: 2,
      }),
    ]);
  });

  it("shows each metric at the precision the API served it at", async () => {
    render(ComparisonReport, { today: "2026-09-02" });
    const duration = await cells("Hypo time / day");
    expect(duration.slice(1, 3)).toEqual(["0m", "30m"]);
    expect(duration[4]).toBe("+30m");
    const events = await cells("Hypo events / day");
    expect(events.slice(1, 3)).toEqual(["0.03", "0.04"]);
    expect(events[4]).toBe("+0.01");
    const gmi = await cells("GMI");
    expect(gmi.slice(1, 3)).toEqual(["7.1%", "7.0%"]);
    expect(gmi[4]).toBe("−0.06 pp");
  });

  it("reads a served zero change as no change", async () => {
    render(ComparisonReport, { today: "2026-09-02" });
    expect((await cells("Hyper time / day"))[4]).toBe("±0m");
    expect((await cells("Hyper events / day"))[4]).toBe("±0");
    expect(await bar("Hyper events / day")).toMatchObject({ flat: true, width: "0%" });
  });

  it("draws a bar for a small change the API did not round away", async () => {
    render(ComparisonReport, { today: "2026-09-02" });
    expect(await bar("GMI")).toMatchObject({ side: "down", width: `${(0.85 / 60) * 50}%` });
  });

  it("scales the bar by the served percent change up to the cap", async () => {
    served = comparison([
      metric(PeriodComparisonMetric.Gri, { valueA: 40, valueB: 52, delta: 12, percentChange: 30, valueDecimals: 0, deltaDecimals: 0 }),
      metric(PeriodComparisonMetric.Cv, { valueA: 20, valueB: 50, delta: 30, percentChange: 150 }),
    ]);
    render(ComparisonReport, { today: "2026-09-02" });
    expect(await bar("Glycemic risk index")).toMatchObject({ side: "up", width: "25%" });
    expect(await bar("Variability (CV)")).toMatchObject({ side: "up", width: "50%" });
  });

  it("fills the half bar in the change's direction when the API served no percent", async () => {
    render(ComparisonReport, { today: "2026-09-02" });
    expect(await bar("Hypo time / day")).toMatchObject({ side: "up", width: "50%" });
    expect((await cells("Hypo time / day"))[4]).toBe("+30m");
  });

  it("shows no data and no change for a metric the API served no value for", async () => {
    served = comparison([
      metric(PeriodComparisonMetric.HypoMinutesPerDay, { valueB: 0, valueDecimals: 0, deltaDecimals: 0 }),
      metric(PeriodComparisonMetric.HypoEventsPerDay, { valueB: 0, valueDecimals: 2, deltaDecimals: 2 }),
    ]);
    render(ComparisonReport, { today: "2026-09-02" });
    const duration = await cells("Hypo time / day");
    expect(duration.slice(1, 3)).toEqual(["No data", "0m"]);
    expect(duration[4]).toBe("—");
    const events = await cells("Hypo events / day");
    expect(events.slice(1, 3)).toEqual(["No data", "0.00"]);
    expect(events[4]).toBe("—");
    expect(await bar("Hypo time / day")).toMatchObject({ flat: true, width: "0%" });
  });
});

describe("grouped comparison rows", () => {
  const percentRows = [
    [PeriodComparisonMetric.TirTarget, "Time in range"],
    [PeriodComparisonMetric.VeryLowPercent, "Time Very Low (%)"],
    [PeriodComparisonMetric.LowPercent, "Time Low (%)"],
    [PeriodComparisonMetric.HighPercent, "Time High (%)"],
    [PeriodComparisonMetric.VeryHighPercent, "Time Very High (%)"],
    [PeriodComparisonMetric.BasalPercent, "Basal share of insulin"],
    [PeriodComparisonMetric.CgmActivePercent, "CGM active time"],
  ] as const;

  const figureRows = [
    [PeriodComparisonMetric.Lbgi, "LBGI"],
    [PeriodComparisonMetric.Hbgi, "HBGI"],
    [PeriodComparisonMetric.Adrr, "ADRR"],
    [PeriodComparisonMetric.BolusesPerDay, "Manual boluses / day"],
    [PeriodComparisonMetric.Icr, "ICR"],
    [PeriodComparisonMetric.Gri, "Glycemic risk index"],
    [PeriodComparisonMetric.DaysWithData, "Days with data"],
    [PeriodComparisonMetric.ReadingsPerDay, "Readings / day"],
  ] as const;

  const glucoseRows = [
    [PeriodComparisonMetric.Mean, "Mean glucose"],
    [PeriodComparisonMetric.Sd, "Standard deviation"],
    [PeriodComparisonMetric.Median, "Median glucose"],
    [PeriodComparisonMetric.Mage, "MAGE"],
    [PeriodComparisonMetric.Conga, "CONGA"],
  ] as const;

  const wholeNumbers = [
    ...glucoseRows.map(([key]) => key),
    PeriodComparisonMetric.CarbsPerDay,
    PeriodComparisonMetric.Gri,
    PeriodComparisonMetric.DaysWithData,
    PeriodComparisonMetric.ReadingsPerDay,
    PeriodComparisonMetric.HyperMinutesPerDay,
    PeriodComparisonMetric.HypoMinutesPerDay,
  ];
  const twoDecimals = [
    PeriodComparisonMetric.Icr,
    PeriodComparisonMetric.HyperEventsPerDay,
    PeriodComparisonMetric.HypoEventsPerDay,
  ];

  const servedDecimals = (key: PeriodComparisonMetric) => {
    if (key === PeriodComparisonMetric.Gmi) return { valueDecimals: 1, deltaDecimals: 2 };
    const digits = wholeNumbers.includes(key) ? 0 : twoDecimals.includes(key) ? 2 : 1;
    return { valueDecimals: digits, deltaDecimals: digits };
  };

  const rising = (key: PeriodComparisonMetric, from: number, to: number) =>
    metric(key, {
      valueA: from,
      valueB: to,
      delta: to - from,
      percentChange: ((to - from) / from) * 100,
      ...servedDecimals(key),
    });

  beforeEach(() => {
    requested = [];
    served = comparison([
      ...percentRows.map(([key]) => rising(key, 50, 55)),
      ...figureRows.map(([key]) => rising(key, 10, 11)),
      ...glucoseRows.map(([key]) => rising(key, 100, 110)),
      rising(PeriodComparisonMetric.InsulinPerDay, 44.2, 46.9),
      rising(PeriodComparisonMetric.BasalPerDay, 20.5, 24.6),
      rising(PeriodComparisonMetric.CarbsPerDay, 150, 180),
      rising(PeriodComparisonMetric.HyperMinutesPerDay, 60, 70),
      rising(PeriodComparisonMetric.HyperEventsPerDay, 2, 3),
      rising(PeriodComparisonMetric.HypoMinutesPerDay, 60, 70),
      rising(PeriodComparisonMetric.HypoEventsPerDay, 2, 3),
      rising(PeriodComparisonMetric.Gmi, 7, 7.5),
      rising(PeriodComparisonMetric.Cv, 30, 33),
    ]);
  });

  it("names ADRR in the time zone note when the API carries a reason", async () => {
    served.timeZoneUnavailableReason = TimeZoneUnavailableReason.NotConfigured;
    render(ComparisonReport, { today: "2026-09-02" });
    await expect.element(page.getByText(/average daily risk range \(ADRR\) is not shown/)).toBeVisible();
    await expect.element(page.getByText(/do not have a time zone/)).toBeVisible();
  });

  it("shows no time zone note when the API carries no reason", async () => {
    render(ComparisonReport, { today: "2026-09-02" });
    await cells("ADRR");
    await expect.element(page.getByText(/average daily risk range \(ADRR\) is not shown/)).not.toBeInTheDocument();
  });

  it("renders each group heading in order, the overview first", async () => {
    render(ComparisonReport, { today: "2026-09-02" });
    await expect.element(page.getByText("Overview", { exact: true })).toBeVisible();
    const headings = [
      "Overview",
      "Range detail",
      "Variability and risk",
      "Insulin and carbs",
      "Data quality",
    ];
    for (const h of headings) {
      await expect.element(page.getByRole("heading", { name: h, level: 3 })).toBeVisible();
    }
    const tops = headings.map((h) => page.getByText(h, { exact: true }).element().getBoundingClientRect().top);
    expect(tops).toEqual([...tops].sort((x, y) => x - y));
  });

  it("shows every served row for both periods with a change", async () => {
    render(ComparisonReport, { today: "2026-09-02" });
    for (const [, label] of [...percentRows, ...figureRows, ...glucoseRows]) {
      const row = await cells(label);
      expect(row[1], `${label} period A`).not.toBe("No data");
      expect(row[2], `${label} period B`).not.toBe("No data");
      expect(row[4], `${label} change`).toMatch(/^\+/);
    }
  });

  it("shows the API's per-day insulin unchanged", async () => {
    render(ComparisonReport, { today: "2026-09-02" });
    const insulin = await cells("Total insulin / day");
    expect(insulin.slice(1, 3)).toEqual(["44.2 U", "46.9 U"]);
    expect(insulin[4]).toBe("+2.7 U");
    expect((await cells("Basal / day")).slice(1, 3)).toEqual(["20.5 U", "24.6 U"]);
    expect((await cells("Carbs / day")).slice(1, 3)).toEqual(["150 g", "180 g"]);
    expect((await cells("ICR")).slice(1, 3)).toEqual(["1:10.0", "1:11.0"]);
  });

  it("converts a served ICR to units per 10 g from the two decimals the API served", async () => {
    served = comparison([
      metric(PeriodComparisonMetric.Icr, {
        valueA: 7.04,
        valueB: 8,
        delta: 0.96,
        percentChange: 13.6,
        valueDecimals: 2,
        deltaDecimals: 2,
      }),
    ]);
    carbRatioFormat.current = "U/10g";
    try {
      render(ComparisonReport, { today: "2026-09-02" });
      expect((await cells("ICR")).slice(1, 3)).toEqual(["1.42 U/10 g", "1.25 U/10 g"]);
    } finally {
      carbRatioFormat.current = "1:x";
    }
  });

  it("shows glucose rows in the units the API served them in", async () => {
    served = comparison(
      [
        metric(PeriodComparisonMetric.Sd, { valueA: 2.2, valueB: 2.4, delta: 0.2, percentChange: 9 }),
      ],
      GlucoseUnit.Mmol
    );
    render(ComparisonReport, { today: "2026-09-02" });
    const sd = await cells("Standard deviation");
    expect(sd.slice(1, 3)).toEqual(["2.2 mmol/L", "2.4 mmol/L"]);
    expect(sd[4]).toBe("+0.2 mmol/L");
  });

  it("asks for glucose in the viewer's unit", async () => {
    glucoseUnits.current = "mmol";
    try {
      render(ComparisonReport, { today: "2026-09-02" });
      await cells("Standard deviation");
      expect(requested.at(-1)?.units).toBe(GlucoseUnit.Mmol);
    } finally {
      glucoseUnits.current = "mg/dl";
    }
  });

  it("shows the personal range row only when the API serves it", async () => {
    render(ComparisonReport, { today: "2026-09-02" });
    await cells("Time Very Low (%)");
    expect(page.getByText("Time in personal range", { exact: true }).elements()).toHaveLength(0);
  });

  it("shows time in the personal range for both periods when served", async () => {
    served.metrics = [
      ...(served.metrics ?? []),
      metric(PeriodComparisonMetric.PersonalRangePercent, {
        valueA: 55,
        valueB: 61.5,
        delta: 6.5,
        percentChange: 11.8,
      }),
    ];
    render(ComparisonReport, { today: "2026-09-02" });
    const row = await cells("Time in personal range");
    expect(row.slice(1, 3)).toEqual(["55.0%", "61.5%"]);
    expect(row[4]).toBe("+6.5 pp");
  });
});
