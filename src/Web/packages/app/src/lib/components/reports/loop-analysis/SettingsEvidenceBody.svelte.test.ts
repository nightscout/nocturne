import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { afterEach, describe, expect, it } from "vitest";
import { SettingsEvidenceConfidence, SettingsEvidenceWithheldReason, TimeZoneUnavailableReason } from "$lib/api";
import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
import WithTooltipProvider from "$lib/test-stubs/WithTooltipProvider.test-stub.svelte";
import SettingsEvidenceBody from "./SettingsEvidenceBody.svelte";
import {
  settingsEvidenceRange,
  settingsEvidenceResponse,
  WITHHELD_SETTINGS_EVIDENCE,
} from "./settings-evidence.test-fixtures";

const renderBody = ({ props }: { props: Record<string, unknown> }) =>
  render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: props } });

/** A block's disclosure, by the hour span its summary begins with. */
const block = (section: string, span: RegExp) =>
  page.getByRole("list", { name: `${section} by block` }).getByRole("listitem").filter({ hasText: span });

/** The episode table's IOB columns need the block's @lg container, wider than the runner's 414px. */
async function atWidth(width: number, body: () => Promise<void>) {
  const original = [window.innerWidth, window.innerHeight] as const;
  await page.viewport(width, 900);
  try {
    await body();
  } finally {
    await page.viewport(original[0], original[1]);
  }
}

describe("SettingsEvidenceBody", () => {
  afterEach(() => {
    glucoseUnits.current = "mg/dl";
  });

  it("states that these are observations, not advice, above every chart", async () => {
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse() } } });

    await expect.element(page.getByText("Observations, not advice")).toBeVisible();
    await expect.element(page.getByText(/Speak to your diabetes care team before you change a setting/)).toBeVisible();
    for (const chart of ["Basal by time of day", "Insulin sensitivity (ISF) by time of day", "Carb ratio by time of day"])
      await expect.element(page.getByRole("slider", { name: chart })).toBeVisible();
  });

  it("shows each block's observed value against the profile's, with n and confidence", async () => {
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse() } } });

    const night = block("Basal", /^\s*2/);
    await expect.element(night.first()).toHaveTextContent(/1\.05\s*U\/h, profile 0\.80/);
    await expect.element(night.first()).toHaveTextContent("n = 14");
    await expect.element(night.first()).toHaveTextContent("Moderate confidence");
    await expect.element(block("Basal", /^\s*4/).first()).toHaveTextContent("Low confidence");
    await expect.element(block("Carb ratio", /^\s*6/).first()).toHaveTextContent(/12\.5\s*g\/U, profile 10\.0/);
    await expect.element(block("Carb ratio", /^\s*6/).first()).toHaveTextContent("High confidence");
  });

  it("says in plain words why a block has no figure, a missing meal in this block", async () => {
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse() } } });

    await expect.element(block("Carb ratio", /^\s*5\s*PM|^\s*17/).first()).toHaveTextContent("No meal in this block has carbs logged");
    await expect.element(block("Insulin sensitivity (ISF)", /^\s*12\s*AM|^\s*0/).first()).toHaveTextContent("No correction boluses in this block");
    await expect.element(block("Insulin sensitivity (ISF)", /^\s*5\s*PM|^\s*17/).first()).toHaveTextContent("Too few episodes to show a figure");
    await expect.element(block("Basal", /^\s*10/).first()).toHaveTextContent("The report did not use any candidates");
  });

  it("opens a block to say why its confidence is not higher, what would raise it, and the candidates it did not use", async () => {
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse() } } });

    const night = block("Basal", /^\s*4/).first();
    await night.getByText(/1\.05/).click();

    await expect.element(night.getByText("Why the confidence is not higher")).toBeVisible();
    await expect.element(night.getByText(/Only a few days have carb entries/)).toBeVisible();
    await expect
      .element(night.getByText("Confidence increases when more days have carb entries. Then the report can find fasting time from the carb entries."))
      .toBeVisible();
    await expect.element(night.getByText(/depends on something that this block cannot check with its own data/)).toBeVisible();
    await expect.element(night.getByText("Carbs within 4 hours before, or during")).toBeVisible();
    await expect.element(night.getByText("The loop's mean correction: +0.25 U/h")).toBeVisible();
    await expect.element(night.getByText("In the same direction on 93% of days")).toBeVisible();
  });

  it("marks a figure towards more insulin on its closed summary row, in words", async () => {
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse() } } });

    const isf = block("Insulin sensitivity (ISF)", /^\s*11/).first();
    await expect.element(isf.getByText("More insulin than your profile")).toBeVisible();
    await expect.element(isf.getByText(/not a setting to copy/)).not.toBeInTheDocument();
    await expect.element(block("Carb ratio", /^\s*6/).first().getByText("More insulin than your profile")).not.toBeInTheDocument();
  });

  it("ends the chart's reading of a figure towards more insulin with the note", async () => {
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse() } } });

    const slider = page.getByRole("slider", { name: "Basal by time of day" });
    (slider.element() as HTMLElement).focus();
    await userEvent.keyboard("{Shift>}{ArrowRight}{/Shift}");
    await expect.element(slider).toHaveAttribute("aria-valuetext", expect.stringMatching(/More insulin than your profile\. See the note\.$/));
  });

  it("opens a figure towards more insulin on the note, and says which way every other figure points", async () => {
    const response = settingsEvidenceResponse();
    response.primary!.basal![2] = { ...response.primary!.basal![2], towardsMoreInsulin: false, towardsLessInsulin: false };
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response } } });

    const isf = block("Insulin sensitivity (ISF)", /^\s*11/).first();
    await isf.getByText(/41/).first().click();
    await expect
      .element(
        isf.getByText(
          "Your data points to more insulin here than your profile gives. This is an observation, not a setting to copy. Usually, people change settings in small steps, one at a time, with their diabetes care team."
        )
      )
      .toBeVisible();

    const cr = block("Carb ratio", /^\s*6/).first();
    await cr.getByText(/12\.5/).first().click();
    await expect.element(cr.getByText("Your data points to less insulin here than your profile gives.")).toBeVisible();
    await expect.element(cr.getByText(/not a setting to copy/)).not.toBeInTheDocument();

    const close = block("Basal", /^\s*4/).first();
    await close.getByText(/1\.05/).click();
    await expect.element(close.getByText("Close to your profile.")).toBeVisible();
  });

  it("says which way a higher value points for every setting", async () => {
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse() } } });

    await expect.element(page.getByText("A higher basal rate means more insulin every hour.")).toBeVisible();
    await expect
      .element(page.getByText("A higher ISF means that each unit decreases glucose more, so less insulin is necessary for the same correction."))
      .toBeVisible();
    await expect.element(page.getByText("A higher carb ratio means less insulin for the same meal.")).toBeVisible();
  });

  it("draws a low-confidence figure quietly, and keys the observed mark even when every figure is low", async () => {
    const response = settingsEvidenceResponse();
    response.primary!.basal = response.primary!.basal!.map((b) => ({ ...b, confidence: SettingsEvidenceConfidence.Low }));
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response } } });

    const low = block("Basal", /^\s*2/).first().getByTestId("observed-value");
    await expect.element(low).not.toHaveClass("font-semibold");
    await expect.element(low).toHaveClass("text-muted-foreground");
    const basal = page.getByRole("region", { name: "Basal" });
    await expect.element(basal.getByTestId("spread-low").first()).toHaveAttribute("stroke-dasharray", "3 2");
    await expect.element(basal.getByTestId("spread-low").first()).toHaveAttribute("stroke-opacity", "0.5");
    await expect.element(basal.getByTestId("spread")).not.toBeInTheDocument();
    await expect.element(basal.getByText("Observed, middle half of episodes")).toBeVisible();
    await expect.element(basal.getByText("Low confidence", { exact: true }).first()).toBeVisible();
  });

  it("says once that carb ratio needs logged meals when no block had any", async () => {
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse({ primary: settingsEvidenceRange({ meals: false }) }) } } });

    await expect.element(page.getByText("Carb ratio needs meals with carbs logged. No meal in this range has carbs logged.")).toBeVisible();
    await expect.element(page.getByRole("list", { name: "Carb ratio by block" })).not.toBeInTheDocument();
    await expect.element(page.getByRole("list", { name: "Basal by block" })).toBeVisible();
  });

  it("leads a narrow episode table with the figure's value and leaves out the IOB columns", async () => {
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse() } } });

    const isf = block("Insulin sensitivity (ISF)", /^\s*11/).first();
    await isf.getByText(/41/).first().click();

    const headers = isf.getByRole("columnheader").elements().map((h) => h.textContent?.trim());
    expect(headers.slice(0, 2)).toEqual(["When", "ISF, mg/dL per U"]);
    await expect.element(isf.getByRole("columnheader", { name: "IOB start / end / residual, U", exact: true })).not.toBeInTheDocument();
    await expect.element(isf.getByRole("rowheader")).toHaveTextContent(/2:00\s*PM|14:00/);
  });

  it("lists a block's episodes with what acted, each opening Day in Review", async () => {
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse() } } });
    await atWidth(1280, async () => {
    const isf = block("Insulin sensitivity (ISF)", /^\s*11/).first();
    await isf.getByText(/41/).first().click();

    for (const heading of [
      "Net insulin, U",
      "IOB start / end / residual, U",
      "Basal need above schedule, U",
      "Insulin that acted, U",
      "Glucose start → end, mg/dL",
    ])
      await expect.element(isf.getByRole("columnheader", { name: heading, exact: true })).toBeVisible();
    await isf.getByRole("button", { name: "IOB start / end / residual, U" }).hover();
    await expect.element(page.getByText(/negative when the loop gave less basal than the schedule/).first()).toBeVisible();
    const link = isf.getByRole("link");
    await expect.element(link).toHaveAttribute("href", "/reports/day-in-review?date=2026-06-12&time=14:00");
    await expect.element(link).toHaveAccessibleName(/in Day in review/);

    const night = block("Basal", /^\s*2/).first();
    await night.getByText(/1\.05/).click();
    await expect.element(night.getByRole("columnheader", { name: "Carried insulin, U/h" })).toBeVisible();
    await expect.element(night.getByRole("columnheader", { name: "Glucose change, mg/dL" })).toBeVisible();
    });
  });

  it("shows ISF in the viewer's units", async () => {
    glucoseUnits.current = "mmol";
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse() } } });

    await expect.element(block("Insulin sensitivity (ISF)", /^\s*11/).first()).toHaveTextContent(/2\.3\s*mmol\/L per U, profile 2\.8/);
  });

  it("adds the compared range beside each figure", async () => {
    renderBody({
      props: { response: settingsEvidenceResponse({ comparison: settingsEvidenceRange({ shift: 0.2, meals: false }) }) },
    });

    await expect.element(block("Basal", /^\s*2/).first()).toHaveTextContent(/compared 1\.25\s*\(n = 14, Moderate confidence\)/);
    const basal = page.getByRole("region", { name: "Basal" });
    await expect.element(basal.getByTestId("observed-compared-low")).toHaveAttribute("fill", "var(--background)");
    await expect.element(basal.getByTestId("compared-spread").first()).toHaveAttribute("stroke-opacity", "0.85");
    await expect.element(basal.getByTestId("observed-compared").first()).toHaveAttribute("d", expect.stringMatching(/Z$/));
    await expect.element(basal.getByText("Compared range, low confidence")).toBeVisible();
    await expect.element(block("Carb ratio", /^\s*6/).first()).toHaveTextContent("compared –");
    await expect.element(page.getByText("Compared range").first()).toBeVisible();
  });

  it("says the page is withheld rather than showing empty evidence", async () => {
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse(WITHHELD_SETTINGS_EVIDENCE) } } });

    await expect.element(page.getByText("Your access does not include the data for this page")).toBeVisible();
    await expect.element(page.getByRole("slider")).not.toBeInTheDocument();
  });

  it("explains a missing time zone", async () => {
    renderBody({
      props: { response: settingsEvidenceResponse({ timeZoneUnavailableReason: TimeZoneUnavailableReason.NotConfigured, primary: undefined }) },
    });
    await expect.element(page.getByText("This report needs a time zone")).toBeVisible();
  });

  it("says a history-clamped viewer's page is withheld because it needs more than 24 hours", async () => {
    renderBody({
      props: {
        response: settingsEvidenceResponse({
          historyClamped: true,
          withheldReason: SettingsEvidenceWithheldReason.HistoryClamped,
          primary: undefined,
        }),
      },
    });
    await expect.element(page.getByText("This page needs more than the last 24 hours")).toBeVisible();
    await expect.element(page.getByText(/You can see only the last 24 hours, so the page shows no figures\./)).toBeVisible();
    await expect.element(page.getByText(/This page uses only the last/)).not.toBeInTheDocument();
    await expect.element(page.getByText("Your access does not include the data for this page")).not.toBeInTheDocument();
    await expect.element(page.getByRole("slider")).not.toBeInTheDocument();
  });

  it("names each rule's span from the thresholds the report serves", async () => {
    const response = settingsEvidenceResponse();
    response.rules = { ...response.rules, basalCarbFreeMs: 3 * 3_600_000, carbRatioWindowMs: 5 * 3_600_000 };
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response } } });

    await expect.element(page.getByText(/insulin that the meal needed in 5 hours\./)).toBeVisible();
    const night = block("Basal", /^\s*4/).first();
    await night.getByText(/1\.05/).click();
    await expect.element(night.getByText("Carbs within 3 hours before, or during")).toBeVisible();
  });

  it("links ISF to the loop's own sensitivity ratio in Loop health", async () => {
    renderBody({
      props: { response: settingsEvidenceResponse(), sensitivityHref: "/reports/loop-analysis/loop-health?days=14#sensitivity" },
    });
    await expect
      .element(page.getByRole("link", { name: /sensitivity ratio by hour, in Loop health/ }))
      .toHaveAttribute("href", "/reports/loop-analysis/loop-health?days=14#sensitivity");
  });

  it("names a filter the viewer's access ignored", async () => {
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse({ ignoredFilters: ["excludeOverrides"] }) } } });
    await expect.element(page.getByText("The report did not use some filters")).toBeVisible();
  });

  it("does not offer Enter on its charts, where Enter opens nothing", async () => {
    render(WithTooltipProvider, { props: { component: SettingsEvidenceBody, componentProps: { response: settingsEvidenceResponse() } } });

    const chart = page.getByRole("slider", { name: "Basal by time of day" });
    await expect.element(chart).toHaveAccessibleDescription(/Use Home and End/);
    await expect.element(chart).not.toHaveAccessibleDescription(/Enter/);
  });
});
