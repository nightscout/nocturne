import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import TIRStackedChart from "./TIRStackedChart.svelte";

describe("TIRStackedChart", () => {
  it("renders a tight-range inset within the target segment (horizontal)", async () => {
    render(TIRStackedChart, {
      props: {
        percentages: { veryLow: 2, low: 3, target: 70, tightTarget: 40, high: 15, veryHigh: 10 },
        orientation: "horizontal",
      },
    });

    await expect.element(page.getByTestId("tight-range-inset")).toBeVisible();
  });

  it("omits the inset when tightTarget is absent", async () => {
    render(TIRStackedChart, {
      props: {
        percentages: { veryLow: 2, low: 3, target: 70, high: 15, veryHigh: 10 },
        orientation: "horizontal",
      },
    });

    await expect.element(page.getByTestId("tight-range-inset")).not.toBeInTheDocument();
  });

  it("renders a tight-range inset within the target segment (vertical)", async () => {
    render(TIRStackedChart, {
      props: {
        percentages: { veryLow: 2, low: 3, target: 70, tightTarget: 40, high: 15, veryHigh: 10 },
        orientation: "vertical",
      },
    });

    // The stacked bar's "In range" percentage label still renders on-chart.
    await expect.element(page.getByText("70%").first()).toBeVisible();
    // The layerchart Chart measures its container via ResizeObserver, which reports
    // zero height in this unsized test harness — pixel geometry is covered by manual
    // verification against a real report page instead. This asserts the conditional
    // render logic fires: the inset exists in the DOM whenever tightTarget is present.
    await expect.element(page.getByTestId("tight-range-inset")).toBeInTheDocument();
  });

  describe("horizontal band key", () => {
    const percentages = { veryLow: 2, low: 3, target: 70, tightTarget: 40, high: 15, veryHigh: 10 };

    it("shows six labelled percentages on screen when keyOnScreen is set", async () => {
      render(TIRStackedChart, {
        props: { percentages, orientation: "horizontal", compact: true, keyOnScreen: true },
      });

      const key = page.getByTestId("band-key");
      await expect.element(key).toBeVisible();
      for (const text of [
        "2% Very low",
        "3% Low",
        "70% In range",
        "40% in tight range",
        "15% High",
        "10% Very high",
      ]) {
        await expect.element(key.getByText(text)).toBeVisible();
      }
    });

    it("keeps the key off screen for a default horizontal bar", async () => {
      render(TIRStackedChart, {
        props: { percentages, orientation: "horizontal", compact: true },
      });

      await expect.element(page.getByTestId("band-key")).not.toBeVisible();
    });

    it("wraps the key onto more than one line inside a 440px container", async () => {
      const target = document.body.appendChild(document.createElement("div"));
      target.style.width = "440px";
      try {
        render(TIRStackedChart, {
          props: { percentages, orientation: "horizontal", compact: true, keyOnScreen: true },
          target,
        });

        const key = page.getByTestId("band-key");
        await expect.element(key).toBeVisible();
        const keyEl = key.element() as HTMLElement;
        const rowHeight = (keyEl.firstElementChild as HTMLElement).offsetHeight;
        expect(keyEl.offsetWidth).toBeLessThanOrEqual(440);
        expect(keyEl.scrollWidth).toBeLessThanOrEqual(440);
        expect(keyEl.offsetHeight).toBeGreaterThan(rowHeight * 1.5);
      } finally {
        target.remove();
      }
    });
  });

  it("captions the served consensus target range", async () => {
    render(TIRStackedChart, {
      props: {
        percentages: { veryLow: 2, low: 3, target: 70, high: 15, veryHigh: 10 },
        thresholds: { low: 63, high: 140 },
      },
    });

    await expect.element(page.getByText("Consensus target range:")).toBeVisible();
    await expect.element(page.getByText(/63\D+140 mg\/dL/)).toBeVisible();
  });

  it("shows no range caption when no thresholds are served", async () => {
    render(TIRStackedChart, {
      props: { percentages: { veryLow: 2, low: 3, target: 70, high: 15, veryHigh: 10 } },
    });

    await expect.element(page.getByText("Consensus target range:")).not.toBeInTheDocument();
  });
});
