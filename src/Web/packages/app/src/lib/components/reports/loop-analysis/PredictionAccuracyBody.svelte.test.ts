import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, expect, it } from "vitest";
import { PredictionCurve, TimeZoneUnavailableReason } from "#lib/api/index.js";
import PredictionAccuracyBody from "./PredictionAccuracyBody.svelte";
import { predictionResponse } from "./prediction-accuracy.test-fixtures";

const NO_DEVICES = ["glucose.read", "treatments.read", "therapy.read", "reports.read"];
const NO_GLUCOSE = ["devices.read", "treatments.read", "therapy.read", "reports.read"];
const NO_TREATMENTS = ["glucose.read", "devices.read", "therapy.read", "reports.read"];

const slider = (curve: string) => page.getByRole("slider", { name: curve, exact: true });

async function focusHorizon(curve: string, steps: number) {
  (slider(curve).element() as HTMLElement).focus();
  for (let i = 0; i < steps; i++) await userEvent.keyboard("{ArrowRight}");
  return page.getByRole("tooltip");
}

const heatmapCell = (value: string) => page.getByRole("cell", { name: value, exact: true }).first();
const tintOf = (value: string) => (heatmapCell(value).element() as HTMLElement).style.getPropertyValue("--tint");

describe("PredictionAccuracyBody", () => {
  it("draws each curve's error by horizon with the figures and the framing", async () => {
    render(PredictionAccuracyBody, { props: { response: predictionResponse() } });

    for (const curve of ["IOB", "Zero temp", "COB", "UAM"]) await expect.element(slider(curve)).toBeVisible();
    await expect.element(page.getByText("4,032")).toBeVisible();
    await expect.element(page.getByText("Trio", { exact: true })).toBeVisible();
    await expect.element(page.getByText(/Only the bias at \+30 min and \+60 min gives information/)).toBeVisible();
    await expect.element(page.getByText(/the average size of the\s+error, in either direction/)).toBeVisible();
    await expect.element(page.getByText(/oref gives its unannounced meal \(UAM\) curve only when/)).toBeVisible();
    await expect.element(page.getByText(/oref removes the end of its zero-temp curve/)).toBeVisible();
    await expect.element(page.getByText(/No bias figure includes interrupted predictions/)).toBeVisible();
    expect(page.getByText(/Faded horizons/).elements()).toHaveLength(0);
  });

  it("reads a horizon's bias, MAE and n as the slider's value and in its tooltip", async () => {
    render(PredictionAccuracyBody, { props: { response: predictionResponse() } });

    const tooltip = await focusHorizon("IOB", 1);
    await expect
      .element(slider("IOB"))
      .toHaveAttribute("aria-valuetext", "+60 min: bias -8 mg/dL, MAE 20 mg/dL, 400 predictions");
    await expect.element(slider("IOB")).toHaveAttribute("aria-describedby", "prediction-tooltip-Iob");
    await expect.element(tooltip.getByText("IOB, +60 min")).toBeVisible();
    await expect.element(tooltip.getByText("400 predictions")).toBeVisible();
    await expect.element(tooltip.getByText(/settings signal/)).toBeVisible();
    expect(tooltip.getByText("MARD").elements()).toHaveLength(0);
  });

  it("moves between horizons with the arrows, Home and End", async () => {
    render(PredictionAccuracyBody, { props: { response: predictionResponse() } });

    await focusHorizon("COB", 2);
    await userEvent.keyboard("{ArrowLeft}");
    await expect.element(page.getByRole("tooltip").getByText("COB, +60 min")).toBeVisible();
    await userEvent.keyboard("{End}");
    await expect.element(page.getByRole("tooltip").getByText("COB, +120 min")).toBeVisible();
    expect(page.getByRole("tooltip").getByText(/settings signal/).elements()).toHaveLength(0);
    await userEvent.keyboard("{Home}");
    await expect.element(slider("COB")).toHaveAttribute("aria-valuenow", "0");
    await expect.element(page.getByRole("tooltip").getByText("COB, +30 min")).toBeVisible();
  });

  it("shows the tooltip for the horizon under the pointer", async () => {
    render(PredictionAccuracyBody, { props: { response: predictionResponse() } });

    await slider("UAM").hover();
    await expect.element(page.getByRole("tooltip").getByText(/^UAM, \+\d+ min$/)).toBeVisible();
    await page.getByText("Error by horizon").hover();
    expect(page.getByRole("tooltip").elements()).toHaveLength(0);
  });

  it("tints the hourly bias by sign and size, and marks the settings-signal columns", async () => {
    render(PredictionAccuracyBody, { props: { response: predictionResponse() } });

    await expect.element(page.getByText("IOB bias by hour of day", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Settings signal", { exact: true }).first()).toBeVisible();
    expect(page.getByText("Settings signal", { exact: true }).elements().length).toBeGreaterThanOrEqual(2);
    // Hour 0's +30 bias is -16, hour 23's is +7.
    expect(tintOf("-16")).toBe("color-mix(in oklab, var(--bias-low) 29%, transparent)");
    expect(tintOf("+7")).toBe("color-mix(in oklab, var(--bias-high) 13%, transparent)");
    expect((heatmapCell("-16").element() as HTMLElement).className).not.toContain("italic");
  });

  it("tints a cell by the bias scale the API serves", async () => {
    const response = predictionResponse();
    const cell = response.primary!.curves![0].byHour![0].horizons![0];
    cell.biasScale = 1;
    render(PredictionAccuracyBody, { props: { response } });

    expect(tintOf("-16")).toBe("color-mix(in oklab, var(--bias-low) 55%, transparent)");
  });

  it("fades and italicises sparse cells, keeping their text in the foreground ink", async () => {
    render(PredictionAccuracyBody, { props: { response: predictionResponse({ sparse: true }) } });

    expect(tintOf("-16")).toBe("color-mix(in oklab, var(--bias-low) 12%, transparent)");
    const cell = heatmapCell("-16").element() as HTMLElement;
    expect(cell.className).toContain("italic");
    expect(cell.className).not.toContain("text-muted-foreground");
    await expect.element(page.getByText(/Faded horizons have too few days/)).toBeVisible();
    const tooltip = await focusHorizon("UAM", 0);
    await expect.element(tooltip.getByText("Too few days to show a pattern")).toBeVisible();
  });

  it("lists each context for the curve picked, with the Adjusted note", async () => {
    render(PredictionAccuracyBody, { props: { response: predictionResponse() } });

    await expect.element(page.getByText("After a correction")).toBeVisible();
    await page.getByRole("radio", { name: "UAM" }).click();
    await expect.element(page.getByText("UAM by context", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Override, temporary target or exercise").first()).toBeVisible();
    await expect.element(page.getByText(/low "eating soon" temporary targets/)).toBeVisible();
  });

  it("outlines the comparison and reads its bias beside the range's", async () => {
    render(PredictionAccuracyBody, { props: { response: predictionResponse({ comparison: true }) } });

    await expect.element(page.getByText("Compared range", { exact: true })).toBeVisible();
    await expect.element(page.getByText("4,032 in the compared range")).toBeVisible();
    await expect.element(page.getByText("The compared range is in the charts above.")).toBeVisible();
    await expect.element(page.getByText("Compared", { exact: true }).first()).toBeVisible();
    const tooltip = await focusHorizon("COB", 0);
    await expect.element(tooltip.getByText("Compared bias")).toBeVisible();
    await expect.element(tooltip.getByText("+2 mg/dL")).toBeVisible();
  });

  it("draws a curve only the comparison carried", async () => {
    const response = predictionResponse({ comparison: true });
    response.comparison!.curves!.push(...predictionResponse({ curves: [PredictionCurve.Default] }).primary!.curves!);
    render(PredictionAccuracyBody, { props: { response } });

    await expect.element(slider("Prediction")).toBeVisible();
    await expect.element(page.getByText("Only in the compared range")).toBeVisible();
    const tooltip = await focusHorizon("Prediction", 0);
    await expect.element(tooltip.getByText("No matched predictions")).toBeVisible();
    await expect.element(tooltip.getByText("Compared bias")).toBeVisible();
  });

  it("hides every curve when the caller cannot read the device data they come from", async () => {
    render(PredictionAccuracyBody, { props: { response: predictionResponse({ scopes: NO_DEVICES }) } });

    await expect.element(page.getByText("Prediction accuracy is hidden")).toBeVisible();
    await expect.element(page.getByText(/Your access does not include\s+device data/)).toBeVisible();
    expect(page.getByRole("slider").elements()).toHaveLength(0);
  });

  it("hides every curve when the caller cannot read glucose", async () => {
    render(PredictionAccuracyBody, { props: { response: predictionResponse({ scopes: NO_GLUCOSE }) } });

    await expect.element(page.getByText("Prediction accuracy is hidden")).toBeVisible();
    await expect.element(page.getByText(/Your access does not\s+include\s+glucose/)).toBeVisible();
    expect(page.getByText(/device data/).elements()).toHaveLength(0);
  });

  it("without treatments keeps the curves read in every context and says why the rest are gone", async () => {
    render(PredictionAccuracyBody, { props: { response: predictionResponse({ scopes: NO_TREATMENTS, interrupted: 7 }) } });

    await expect.element(slider("UAM")).toBeVisible();
    expect(slider("IOB").elements()).toHaveLength(0);
    await expect.element(page.getByText(/These curves are hidden:\s+IOB, Zero temp, COB\./)).toBeVisible();
    await expect.element(page.getByText(/your access does not\s+include treatments/)).toBeVisible();
    expect(page.getByText("After a correction").elements()).toHaveLength(0);
    expect(page.getByText("Interrupted", { exact: true }).elements()).toHaveLength(0);
  });

  it("counts interrupted horizons in the context table", async () => {
    render(PredictionAccuracyBody, {
      props: { response: predictionResponse({ curves: [PredictionCurve.Default], interrupted: 7 }) },
    });

    await expect.element(page.getByText("Interrupted", { exact: true }).first()).toBeVisible();
    await expect.element(page.getByText(/The bias, MAE and n do not include interrupted predictions/)).toBeVisible();
    expect(page.getByText(/curves? (is|are) hidden/).elements()).toHaveLength(0);
  });

  it("names the filters the caller's access ignored", async () => {
    render(PredictionAccuracyBody, { props: { response: { ...predictionResponse(), ignoredFilters: ["excludeExercise", "startDate"] } } });

    await expect.element(page.getByText("The report did not use some filters")).toBeVisible();
    await expect.element(page.getByText(/so the report did not use them:\s+Exercise\./)).toBeVisible();
  });

  it("explains a missing time zone in place of the figures", async () => {
    render(PredictionAccuracyBody, {
      props: {
        response: {
          ...predictionResponse(),
          primary: { curves: [] },
          timeZoneUnavailableReason: TimeZoneUnavailableReason.NotConfigured,
        },
      },
    });

    await expect.element(page.getByText("This report needs a time zone")).toBeVisible();
    expect(page.getByText("Predictions analyzed").elements()).toHaveLength(0);
  });

  it("says a clamped viewer sees only the last 24 hours", async () => {
    render(PredictionAccuracyBody, { props: { response: predictionResponse({ historyClamped: true }) } });

    await expect.element(page.getByText(/This page uses only the last 24 hours/)).toBeVisible();
    await expect.element(page.getByText(/adds the most recent predictions when their readings arrive/)).toBeVisible();
  });

  it("says when the range has no predictions, without suggesting a longer range to a clamped viewer", async () => {
    const view = render(PredictionAccuracyBody, { props: { response: predictionResponse({ curves: [] }) } });
    await expect.element(page.getByText("No predictions in this range")).toBeVisible();
    await expect.element(page.getByText(/Select a longer range/)).toBeVisible();
    view.unmount();

    render(PredictionAccuracyBody, { props: { response: predictionResponse({ curves: [], historyClamped: true }) } });
    await expect.element(page.getByText(/they have no predictions yet/)).toBeVisible();
    expect(page.getByText(/Select a longer range/).elements()).toHaveLength(0);
  });
});
