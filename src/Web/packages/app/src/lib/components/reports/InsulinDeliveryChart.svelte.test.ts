import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import type { HourlyInsulinDeliveryResponse } from "#lib/api/index.js";
import InsulinDeliveryChart from "./InsulinDeliveryChart.svelte";

const period = (units: number, percent: number) => ({ units, percent });

const delivery = (profileBasalRate: number): HourlyInsulinDeliveryResponse => ({
  hours: Array.from({ length: 24 }, (_, hour) => ({
    hour,
    scheduledBasal: 0.5,
    tempBasal: 0,
    basal: 0.5,
    manualBolus: 0.25,
    automaticBolus: 0,
    total: 0.75,
    count: 1,
    profileBasalRate,
  })),
  totalDeliveryPeriods: {
    morning: period(4.51, 25),
    afternoon: period(3.33, 18.5),
    eveningNight: period(10.16, 56.5),
    dailyTotal: 18,
  },
  basalPeriods: {
    morning: period(3.01, 25),
    afternoon: period(3.01, 25),
    eveningNight: period(6.02, 50),
    dailyTotal: 12.04,
  },
  daysWithData: 7,
});

describe("InsulinDeliveryChart", () => {
  it("shows the backend's period totals for total delivery to two decimals", async () => {
    render(InsulinDeliveryChart, { props: { delivery: delivery(0.6), showStacked: true } });

    await expect.element(page.getByText("4.51")).toBeVisible();
    await expect.element(page.getByText("3.33")).toBeVisible();
    await expect.element(page.getByText("10.16")).toBeVisible();
    await expect.element(page.getByText("Daily total 18.00 U")).toBeVisible();
  });

  it("shows the basal periods in the basal-only variant", async () => {
    render(InsulinDeliveryChart, { props: { delivery: delivery(0.6), showStacked: false } });

    await expect.element(page.getByText("6.02")).toBeVisible();
    await expect.element(page.getByText("Daily total 12.04 U")).toBeVisible();
  });

  it("keys the scheduled basal rate line when the backend serves a profile rate", async () => {
    const { container } = render(InsulinDeliveryChart, {
      props: { delivery: delivery(0.6), showStacked: true },
    });

    await expect.element(page.getByText("Scheduled basal rate")).toBeVisible();
    await expect
      .poll(() => container.querySelectorAll('path[stroke-dasharray="6 3"]').length)
      .toBe(1);
    const d = container.querySelector('path[stroke-dasharray="6 3"]')?.getAttribute("d") ?? "";
    const xs: number[] = Array.from(d.matchAll(/[ML]([\d.]+),/g), (m) => Number(m[1]));
    expect(xs[0]).toBe(0);
    expect(xs.at(-1)).toBeGreaterThan(xs.at(-3) ?? Infinity);
    expect(new Set(xs).size).toBe(23 + 2);
  });

  it("omits the scheduled basal rate key when no profile rate is served", async () => {
    const { container } = render(InsulinDeliveryChart, {
      props: { delivery: delivery(0), showStacked: true },
    });

    await expect.element(page.getByText("Scheduled basal rate")).not.toBeInTheDocument();
    expect(container.querySelectorAll('path[stroke-dasharray="6 3"]')).toHaveLength(0);
  });
});
