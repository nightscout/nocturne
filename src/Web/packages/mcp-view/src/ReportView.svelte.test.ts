import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import "./app.css";
import ReportView from "./ReportView.svelte";

const from = Date.UTC(2026, 9, 29, 6);
const MINUTE = 60 * 1000;
const readings = 36;

/** What the API's show_glucose tool returns as structuredContent, cut down. */
const structuredContent = {
  kind: "glucose",
  units: "mg/dl",
  window: { from, to: from + 3 * 60 * MINUTE },
  omittedTracks: ["heart rate"],
  data: {
    glucoseData: Array.from({ length: readings }, (_, i) => ({
      time: from + i * 5 * MINUTE,
      sgv: 90 + i * 5,
      direction: "Flat",
    })),
    thresholds: { low: 70, high: 180, veryLow: 54, veryHigh: 250, glucoseYMax: 300 },
    bolusMarkers: [{ time: from + 60 * MINUTE, insulin: 2.5, treatmentId: "b1", bolusType: "Normal" }],
  },
};

describe("ReportView", () => {
  it("draws a point per reading from the tool result", async () => {
    render(ReportView, { result: { structuredContent }, locale: "en-US" });

    const plot = page.getByTestId("glucose-plot");
    await expect.element(plot).toBeVisible();
    await expect.element(page.getByText("2.5U")).toBeInTheDocument();
    await expect
      .poll(() => plot.element().querySelectorAll("circle").length)
      .toBeGreaterThanOrEqual(readings);
  });

  it("waits for the result before drawing", async () => {
    render(ReportView, { result: null, locale: "en-US" });

    await expect.element(page.getByText("Waiting for the glucose data.")).toBeVisible();
  });

  it("says so when the result carries no chart data", async () => {
    render(ReportView, { result: { structuredContent: { kind: "other" } }, locale: "en-US" });

    await expect.element(page.getByRole("alert")).toHaveTextContent("no chart data");
  });
});
