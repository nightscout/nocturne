import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, expect, it, vi } from "vitest";
import { page as appPage } from "$app/state";
import { applyPreferences } from "$lib/stores/appearance-store.svelte";
import { reportsOverviewScopes } from "$lib/navigation/report-navigation.svelte";

vi.mock("$api/reports.remote", () => ({ getReportsData: vi.fn() }));
vi.mock("$lib/hooks/date-params.svelte", () => ({
  requireDateParamsContext: () => ({ dateRangeInput: {} }),
}));
vi.mock("$lib/hooks/resource-context.svelte", () => ({
  contextResource: () => ({
    loading: false,
    error: null,
    date: { from: new Date(2025, 0, 1), to: new Date(2025, 0, 14) },
    current: {
      entries: [],
      analysis: {
        basicStats: { mean: 154 },
        glycemicVariability: {
          estimatedA1cDisplay: { percent: 7, mmolMol: 53 },
          coefficientOfVariation: 33,
        },
        timeInRange: { percentages: { low: 3, veryLow: 1 } },
      },
    },
  }),
}));

import ReportsPage from "./+page.svelte";

beforeEach(() => {
  appPage.data.effectivePermissions = reportsOverviewScopes;
  applyPreferences({ a1cName: "A1c", a1cUnits: "mmol/mol" });
});

it("changes only A1c units while CV and time below range remain percentages", async () => {
  render(ReportsPage);
  await expect.element(page.getByText(/^53\s*mmol\/mol$/)).toBeVisible();
  await expect.element(page.getByText(/^33\s*%$/)).toBeVisible();
  await expect.element(page.getByText(/^4\.0\s*%$/)).toBeVisible();
});
