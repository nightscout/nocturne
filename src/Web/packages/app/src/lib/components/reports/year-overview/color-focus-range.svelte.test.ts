import { describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import Harness from "./ColorFocusRange.test-harness.svelte";

const minimumInput = (metric = "TDD") =>
  page.getByRole("spinbutton", { name: `${metric} minimum color value` });
const maximumInput = (metric = "TDD") =>
  page.getByRole("spinbutton", { name: `${metric} maximum color value` });
const minimumSlider = () =>
  page.getByRole("slider", { name: "TDD minimum color value" });
const maximumSlider = () =>
  page.getByRole("slider", { name: "TDD maximum color value" });

describe("year overview color focus", () => {
  it("reuses base steps when only the focus changes", async () => {
    const precision = vi.spyOn(Number.prototype, "toPrecision");
    try {
      const screen = render(Harness);
      await expect.element(maximumInput()).toHaveValue(500);
      const initialConversions = precision.mock.calls.length;
      expect(initialConversions).toBeGreaterThan(1000);

      await minimumInput().fill("10.25");
      await maximumInput().fill("70.75");
      await expect.element(maximumInput()).toHaveValue(70.75);
      expect(precision.mock.calls.length).toBe(initialConversions);

      await screen.rerender({ observedMax: 800 });
      await expect
        .element(maximumSlider())
        .toHaveAttribute("aria-valuemax", "800");
      expect(precision.mock.calls.length).toBeGreaterThan(initialConversions);
      await expect.element(minimumInput()).toHaveValue(10.25);
      await expect.element(maximumInput()).toHaveValue(70.75);
    } finally {
      precision.mockRestore();
    }
  });

  it("focuses the legend on exact numeric limits within an outlier-sized axis", async () => {
    const { container } = render(Harness);

    await minimumInput().fill("10");
    await maximumInput().fill("70");

    await expect
      .element(page.getByTestId("selected-range"))
      .toHaveTextContent("[10,70]");
    await expect
      .element(minimumSlider())
      .toHaveAttribute("aria-valuenow", "10");
    await expect
      .element(maximumSlider())
      .toHaveAttribute("aria-valuenow", "70");
    await expect
      .element(maximumSlider())
      .toHaveAttribute("aria-valuemax", "500");
    const track = container.querySelector<HTMLElement>(
      "[data-color-focus-track]"
    );
    expect(track?.style.background).toContain("2%");
    expect(track?.style.background).toMatch(/14(?:\.0+2)?%/);
  });

  it("allows decimal limits without rounding the selected values", async () => {
    render(Harness);

    await minimumInput().fill("10.25");
    await maximumInput().fill("70.75");

    await expect
      .element(page.getByTestId("selected-range"))
      .toHaveTextContent("[10.25,70.75]");
    await expect.element(minimumInput()).toHaveValue(10.25);
    await expect.element(maximumInput()).toHaveValue(70.75);
  });

  it.each([12.35, 0.5])(
    "keeps fractional automatic maximum %s without creating a manual preference",
    async (observedMax) => {
      const screen = render(Harness, { observedMax });
      const { container } = screen;

      await expect.element(maximumInput()).toHaveValue(observedMax);
      await expect
        .element(maximumSlider())
        .toHaveAttribute("aria-valuenow", String(observedMax));
      await expect
        .element(page.getByTestId("selected-range"))
        .toHaveTextContent("null");
      if (observedMax === 0.5) {
        expect(
          container.querySelector<HTMLElement>("[data-color-focus-track]")
            ?.style.background
        ).toContain("50%");
      }
      await screen.rerender({ observedMax: observedMax + 1 });
      await expect.element(maximumInput()).toHaveValue(observedMax + 1);
      await expect
        .element(page.getByTestId("selected-range"))
        .toHaveTextContent("null");
    }
  );

  it("keeps controls responsive for very large manually entered maxima", async () => {
    render(Harness);

    await maximumInput().fill("1000000000");
    await expect
      .element(maximumSlider())
      .toHaveAttribute("aria-valuemax", "1000000000");
    await expect.element(maximumInput()).toHaveValue(1_000_000_000);
    await page
      .getByRole("button", { name: "Reset TDD color range to automatic" })
      .click();
    await expect.element(maximumInput()).toHaveValue(500);
    await expect
      .element(page.getByTestId("selected-range"))
      .toHaveTextContent("null");
  });

  it("moves handles by keyboard without crossing or collapsing the range", async () => {
    render(Harness, { initialRange: [10, 10.2] });

    (minimumSlider().element() as HTMLElement).focus();
    await userEvent.keyboard("{ArrowRight}");
    await expect
      .element(minimumSlider())
      .toHaveAttribute("aria-valuenow", "10.1");
    await userEvent.keyboard("{ArrowRight}{End}");
    await expect
      .element(minimumSlider())
      .toHaveAttribute("aria-valuenow", "10.1");

    (maximumSlider().element() as HTMLElement).focus();
    await userEvent.keyboard("{ArrowLeft}{Home}");
    await expect
      .element(maximumSlider())
      .toHaveAttribute("aria-valuenow", "10.2");
    await expect
      .element(page.getByTestId("selected-range"))
      .toHaveTextContent("[10.1,10.2]");
  });

  it("drags the upper handle on the color bar at mobile width", async () => {
    const originalSize = [window.innerWidth, window.innerHeight] as const;
    await page.viewport(390, 700);
    try {
      const { container } = render(Harness, { initialRange: [10, 70] });
      const track = container.querySelector<HTMLElement>(
        "[data-color-focus-track]"
      )!;
      const bounds = track.getBoundingClientRect();
      expect(bounds.width).toBeGreaterThan(300);
      expect(bounds.height).toBeGreaterThan(10);

      await page.screenshot({ path: "test-results/color-focus-mobile.png" });
      await userEvent.dragAndDrop(maximumSlider(), track, {
        targetPosition: { x: bounds.width * 0.6, y: bounds.height / 2 },
      });

      await expect.element(minimumInput()).toHaveValue(10);
      const maximum = (maximumInput().element() as HTMLInputElement)
        .valueAsNumber;
      expect(maximum).toBeGreaterThan(299);
      expect(maximum).toBeLessThan(301);
    } finally {
      await page.viewport(originalSize[0], originalSize[1]);
    }
  });

  it.each(["", "-1", "70", "80", "1e309"])(
    "rejects invalid minimum %j without changing the active colors",
    async (value) => {
      render(Harness, { initialRange: [10, 70] });

      await minimumInput().fill(value);

      await expect
        .element(minimumInput())
        .toHaveAttribute("aria-invalid", "true");
      await expect.element(page.getByRole("alert")).toBeVisible();
      await expect
        .element(page.getByTestId("selected-range"))
        .toHaveTextContent("[10,70]");
      await expect
        .element(minimumSlider())
        .toHaveAttribute("aria-valuenow", "10");
    }
  );

  it("rejects a TIR maximum above 100 percent", async () => {
    render(Harness, {
      metricLabel: "Time in Range",
      unit: "%",
      fixedMax: 100,
      initialRange: [10, 70],
    });

    await maximumInput("Time in Range").fill("101");

    await expect
      .element(maximumInput("Time in Range"))
      .toHaveAttribute("aria-invalid", "true");
    await expect
      .element(page.getByRole("alert"))
      .toHaveTextContent("up to 100 %");
    await expect
      .element(page.getByTestId("selected-range"))
      .toHaveTextContent("[10,70]");
  });

  it("resets invalid drafts even when the range is already automatic", async () => {
    render(Harness);
    await minimumInput().fill("600");
    await expect.element(page.getByRole("alert")).toBeVisible();

    await page
      .getByRole("button", { name: "Reset TDD color range to automatic" })
      .click();

    await expect.element(page.getByRole("alert")).not.toBeInTheDocument();
    await expect.element(minimumInput()).toHaveValue(0);
    await expect.element(maximumInput()).toHaveValue(500);
    await expect
      .element(page.getByTestId("selected-range"))
      .toHaveTextContent("null");
  });

  it("clears an invalid draft when switching metrics with the same automatic maximum", async () => {
    const screen = render(Harness);
    await minimumInput().fill("600");
    await expect.element(page.getByRole("alert")).toBeVisible();

    await screen.rerender({ metricLabel: "Basal" });

    await expect.element(page.getByRole("alert")).not.toBeInTheDocument();
    await expect.element(minimumInput("Basal")).toHaveValue(0);
    await expect.element(maximumInput("Basal")).toHaveValue(500);
  });

  it("preserves manual limits across observed-data changes and restores Auto", async () => {
    const screen = render(Harness, { initialRange: [10, 70] });

    await screen.rerender({ observedMax: 800 });
    await expect
      .element(maximumSlider())
      .toHaveAttribute("aria-valuemax", "800");
    await expect.element(minimumInput()).toHaveValue(10);
    await expect.element(maximumInput()).toHaveValue(70);

    await screen.rerender({ observedMax: 40 });
    await expect
      .element(maximumSlider())
      .toHaveAttribute("aria-valuemax", "70");
    await expect
      .element(page.getByTestId("selected-range"))
      .toHaveTextContent("[10,70]");

    await page
      .getByRole("button", { name: "Reset TDD color range to automatic" })
      .click();
    await expect.element(minimumInput()).toHaveValue(0);
    await expect.element(maximumInput()).toHaveValue(40);
    await expect
      .element(page.getByTestId("selected-range"))
      .toHaveTextContent("null");
  });
});
