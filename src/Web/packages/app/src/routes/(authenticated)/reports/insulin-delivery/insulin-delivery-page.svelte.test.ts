import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const hoisted = vi.hoisted(() => ({
  asked: {} as Record<string, unknown[]>,
}));

vi.mock("#lib/api/reports.remote.js", () => {
  const read = (name: string, served: unknown) => (input: unknown) => {
    (hoisted.asked[name] ??= []).push(input);
    return remoteQuery(() => served);
  };
  return {
    getInsulinDeliveryReport: read("insulinDelivery", { tdd: 40 }),
    getDailyBasalBolusReport: read("dailyBasalBolus", {}),
    getHourlyDeliveryReport: read("hourlyDelivery", {}),
  };
});

vi.mock("#lib/hooks/date-params.svelte.js", () => ({
  requireDateParamsContext: () => ({
    fromDay: "2025-06-01",
    toDay: "2025-06-14",
    startDate: new Date(2025, 5, 1),
    endDate: new Date(2025, 5, 14, 23, 59, 59, 999),
    dayCount: 14,
  }),
}));

vi.mock("#lib/components/reports/BasalBolusRatioChart.svelte", async () => ({
  default: (await import("#lib/test-stubs/Empty.test-stub.svelte")).default,
}));
vi.mock("#lib/components/reports/InsulinDeliveryChart.svelte", async () => ({
  default: (await import("#lib/test-stubs/Empty.test-stub.svelte")).default,
}));

import InsulinDeliveryPage from "./+page.svelte";

describe("insulin delivery report", () => {
  beforeEach(() => {
    hoisted.asked = {};
  });

  it("asks every read for the picked days, leaving the instants to the server's reading of the patient's zone", async () => {
    render(InsulinDeliveryPage);

    await expect.element(page.getByText("Avg TDD")).toBeVisible();
    for (const asked of Object.values(hoisted.asked)) {
      expect(asked.at(-1)).toEqual({ from: "2025-06-01", to: "2025-06-14" });
    }
    expect(Object.keys(hoisted.asked).sort()).toEqual([
      "dailyBasalBolus",
      "hourlyDelivery",
      "insulinDelivery",
    ]);
  });
});
