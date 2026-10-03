import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, expect, it, vi } from "vitest";
import type { GlycemicVariability } from "$api/generated/nocturne-api-client";
import { applyPreferences } from "$lib/stores/appearance-store.svelte";

const report = vi.hoisted(() => ({
  variability: null as GlycemicVariability | null,
}));

vi.mock("$api/reports.remote", () => ({ getReportsData: vi.fn() }));
vi.mock("$lib/hooks/date-params.svelte", () => ({
  requireDateParamsContext: () => ({ dateRangeInput: {} }),
}));
vi.mock("$lib/hooks/resource-context.svelte", () => ({
  contextResource: () => ({
    date: { dayCount: 14 },
    current: {
      entries: [],
      analysis: {
        basicStats: { mean: 154 },
        glycemicVariability: report.variability,
        timeInRange: { percentages: { target: 70, low: 3, veryLow: 1 } },
      },
    },
  }),
}));

import ExecutiveSummary from "./+page.svelte";

beforeEach(() => {
  report.variability = null;
  applyPreferences({ a1cName: "HbA1c", a1cUnits: "percent" });
});

it.each(["percent", "mmol/mol"] as const)(
  "omits the A1c target when variability is unavailable in %s",
  async (a1cUnits) => {
    applyPreferences({ a1cUnits });
    render(ExecutiveSummary);
    await expect.element(page.getByText("No estimate for this window")).toBeVisible();
    await expect.element(page.getByText(/^Target: <(?:–|7\.0%|53 mmol\/mol)$/)).not.toBeInTheDocument();
  }
);

it.each([
  { a1cUnits: "percent" as const, target: "Target: <7.0%" },
  { a1cUnits: "mmol/mol" as const, target: "Target: <53 mmol/mol" },
])("shows the backend target in $a1cUnits", async ({ a1cUnits, target }) => {
  report.variability = {
    estimatedA1c: 7,
    estimatedA1cDisplay: { percent: 7, mmolMol: 53.00565 },
    a1cTarget: { percent: 7, mmolMol: 53.00565 },
  };
  applyPreferences({ a1cUnits });
  render(ExecutiveSummary);
  await expect.element(page.getByText(target, { exact: true })).toBeVisible();
});
