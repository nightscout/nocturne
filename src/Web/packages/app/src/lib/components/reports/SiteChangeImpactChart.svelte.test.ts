import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import type { SiteChangeImpactAnalysis } from "$lib/api";
import Harness from "./SiteChangeImpactNarrowHarness.test.svelte";

const analysis = (hoursBefore: number, hoursAfter: number): SiteChangeImpactAnalysis => ({
  hasSufficientData: true,
  siteChangeCount: 5,
  hoursBeforeChange: hoursBefore,
  hoursAfterChange: hoursAfter,
  dataPoints: Array.from({ length: hoursBefore + hoursAfter + 1 }, (_, i) => ({
    minutesFromChange: (i - hoursBefore) * 60,
    averageGlucose: 120,
    medianGlucose: 120,
    stdDev: 10,
    count: 5,
    percentile10: 90,
    percentile25: 105,
    percentile75: 135,
    percentile90: 150,
  })),
});

describe("SiteChangeImpactChart x axis", () => {
  it.each([260, 340, 480])("keeps the Site Change label at %ipx wide", async (width) => {
    render(Harness, { props: { analysis: analysis(12, 24), width } });

    await expect.element(page.getByText("Site change", { exact: true })).toBeVisible();
  });
});
