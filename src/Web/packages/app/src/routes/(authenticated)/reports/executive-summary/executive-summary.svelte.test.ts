import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, expect, it, vi } from "vitest";
import { TargetStatus, type GlycemicVariability } from "$api/generated/nocturne-api-client";
import { applyPreferences } from "$lib/stores/appearance-store.svelte";
import { TimeZoneUnavailableReason } from "$lib/api";

const report = vi.hoisted(() => ({
  variability: null as GlycemicVariability | null,
  cvStatus: undefined as TargetStatus | undefined,
  reason: undefined as TimeZoneUnavailableReason | undefined,
  thresholds: { veryLow: 54, low: 63, targetBottom: 63, targetTop: 140, high: 140, veryHigh: 250 },
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
      timeZoneUnavailableReason: report.reason,
      analysis: {
        reliability: { meetsReliabilityCriteria: false, daysOfData: 3, recommendedMinimumDays: 14 },
        basicStats: { mean: 154 },
        glycemicVariability: report.variability,
        clinicalAssessment: { cvAssessment: { status: report.cvStatus } },
        timeInRange: {
          thresholds: report.thresholds,
          percentages: { target: 70, low: 3, veryLow: 1, belowRange: 4, aboveRange: 26 },
        },
      },
    },
  }),
}));

import ExecutiveSummary from "./+page.svelte";

const cardWith = (container: HTMLElement, text: string) => {
  const card = [...container.querySelectorAll<HTMLElement>('[data-slot="card"]')].find((element) =>
    element.textContent?.includes(text)
  );
  if (!card) throw new Error(`No card contains "${text}"`);
  return page.elementLocator(card);
};

beforeEach(() => {
  report.variability = null;
  report.cvStatus = undefined;
  report.reason = undefined;
  applyPreferences({ a1cName: "HbA1c", a1cUnits: "percent" });
});

it("names the consensus target range and the edges the API measured", async () => {
  applyPreferences({ glucoseUnits: "mg/dl" });
  render(ExecutiveSummary);
  await expect
    .element(page.getByText("Percentage of time in the consensus target range (63-140 mg/dL)"))
    .toBeVisible();
  await expect.element(page.getByText("Time spent below 63 mg/dL (target: <4%)")).toBeVisible();
  await expect.element(page.getByText("Time spent above 140 mg/dL (target: <25%)")).toBeVisible();
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

it.each([
  { a1cMet: true, cvMet: false, a1cState: "Target met", cvState: "Target not met" },
  { a1cMet: false, cvMet: true, a1cState: "Target not met", cvState: "Target met" },
])(
  "leads each card with its own value, target and state (eA1c $a1cState, CV $cvState)",
  async ({ a1cMet, cvMet, a1cState, cvState }) => {
    report.variability = {
      estimatedA1c: 6.4,
      estimatedA1cDisplay: { percent: 6.4, mmolMol: 46.3 },
      a1cTarget: { percent: 7, mmolMol: 53.00565 },
      estimatedA1cMeetsTarget: { percent: a1cMet, mmolMol: a1cMet },
      coefficientOfVariation: 31.6,
      coefficientOfVariationTarget: 36,
    };
    report.cvStatus = cvMet ? TargetStatus.Met : TargetStatus.NotMet;
    const { container } = render(ExecutiveSummary);

    const a1cCard = cardWith(container, "What is eHbA1c?");
    await expect.element(a1cCard.getByText("6.4%", { exact: true })).toBeVisible();
    await expect.element(a1cCard.getByText("Target: below 7.0%", { exact: true })).toBeVisible();
    await expect.element(a1cCard.getByText(a1cState, { exact: true })).toBeVisible();

    const cvCard = cardWith(container, "Glucose stability");
    await expect.element(cvCard.getByText("32%", { exact: true })).toBeVisible();
    await expect.element(cvCard.getByText("Target: ≤36%", { exact: true })).toBeVisible();
    await expect.element(cvCard.getByText(cvState, { exact: true })).toBeVisible();
  }
);

it.each([
  { a1cUnits: "percent" as const, shown: "7.0%", target: "Target: below 7.0%", state: "Target not met" },
  { a1cUnits: "mmol/mol" as const, shown: "52 mmol/mol", target: "Target: below 53 mmol/mol", state: "Target met" },
])("judges the eA1c state in the displayed unit ($a1cUnits)", async ({ a1cUnits, shown, target, state }) => {
  report.variability = {
    estimatedA1c: 6.95,
    estimatedA1cDisplay: { percent: 6.95, mmolMol: 52.459 },
    a1cTarget: { percent: 7, mmolMol: 53.00565 },
    estimatedA1cMeetsTarget: { percent: false, mmolMol: true },
  };
  applyPreferences({ a1cUnits });
  const { container } = render(ExecutiveSummary);

  const a1cCard = cardWith(container, "What is eHbA1c?");
  await expect.element(a1cCard.getByText(shown, { exact: true })).toBeVisible();
  await expect.element(a1cCard.getByText(target, { exact: true })).toBeVisible();
  await expect.element(a1cCard.getByText(state, { exact: true })).toBeVisible();
});

it("shows no CV state when the assessment did not assess it", async () => {
  report.variability = { coefficientOfVariation: 31.6, coefficientOfVariationTarget: 36 };
  report.cvStatus = TargetStatus.NotAssessed;
  const { container } = render(ExecutiveSummary);
  const cvCard = cardWith(container, "Glucose stability");
  await expect.element(cvCard.getByText("32%", { exact: true })).toBeVisible();
  await expect.element(cvCard.getByText(/^Target (not )?met$/)).not.toBeInTheDocument();
});

it("shows no target state when the variability is unavailable", async () => {
  render(ExecutiveSummary);
  await expect.element(page.getByText(/^Target (not )?met$/)).not.toBeInTheDocument();
});

it("says the days of data are UTC days when range analytics carries a reason", async () => {
  report.reason = TimeZoneUnavailableReason.NotConfigured;
  render(ExecutiveSummary);
  await expect.element(page.getByText("Days of data are counted on UTC days.")).toBeVisible();
});

it("shows the reliability badge without a note when range analytics carries no reason", async () => {
  render(ExecutiveSummary);
  await expect.element(page.getByText(/Based on 3 days of data/)).toBeVisible();
  await expect.element(page.getByText("Days of data are counted on UTC days.")).not.toBeInTheDocument();
});
