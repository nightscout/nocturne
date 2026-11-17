import { describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import Harness from "./ColorFocusRange.test-harness.svelte";

function elementOf<T extends Element>(
  locator: { element(): Element },
  type: new () => T
): T {
  const element = locator.element();
  if (!(element instanceof type)) throw new TypeError(`Expected a ${type.name}`);
  return element;
}

const minimumInput = (metric = "TDD") =>
  page.getByRole("spinbutton", { name: `${metric} focus minimum` });
const maximumInput = (metric = "TDD") =>
  page.getByRole("spinbutton", { name: `${metric} focus maximum` });
const minimumSlider = (metric = "TDD") =>
  page.getByRole("slider", { name: `${metric} focus minimum` });
const maximumSlider = (metric = "TDD") =>
  page.getByRole("slider", { name: `${metric} focus maximum` });
const boundaryInput = (n: number, metric = "TDD") =>
  page.getByRole("spinbutton", { name: `${metric} color boundary ${n}`, exact: true });
const boundarySlider = (n: number, metric = "TDD") =>
  page.getByRole("slider", { name: `${metric} color boundary ${n}`, exact: true });
const addBoundary = () => page.getByRole("button", { name: "Color boundary", exact: true });
const selected = (what: "window" | "boundaries" | "ends") => page.getByTestId(`selected-${what}`);
function pick(label: string, hex: string) {
  const input = elementOf(page.getByLabelText(label, { exact: true }), HTMLInputElement);
  input.value = hex;
  input.dispatchEvent(new Event("input", { bubbles: true }));
}
const gradient = (id = "color-focus-track") =>
  elementOf(page.getByTestId(id), HTMLElement).style.getPropertyValue("--scale-gradient");

describe("year overview color focus", () => {
  it("shows only the focus window until a color boundary is added", async () => {
    render(Harness);
    await expect.element(minimumInput()).toHaveValue(0);
    await expect.element(maximumInput()).toHaveValue(500);
    expect(page.getByRole("slider").elements()).toHaveLength(2);
    await expect.element(selected("window")).toHaveTextContent("null");
  });

  it("reuses base steps when only the window changes", async () => {
    const precision = vi.spyOn(Number.prototype, "toPrecision");
    try {
      const screen = render(Harness);
      await expect.element(maximumInput()).toHaveValue(500);
      const initialConversions = precision.mock.calls.length;
      expect(initialConversions).toBeGreaterThan(1000);

      await minimumInput().fill("10.25");
      await maximumInput().fill("70.75");
      await expect.element(maximumInput()).toHaveValue(70.75);
      // A rebuild costs one call per step, thousands here; a handful come from rounding the next boundary.
      expect(precision.mock.calls.length).toBeLessThan(initialConversions + 100);

      await screen.rerender({ observedMax: 800 });
      await expect.element(maximumSlider()).toHaveAttribute("aria-valuemax", "800");
      await expect.element(minimumInput()).toHaveValue(10.25);
    } finally {
      precision.mockRestore();
    }
  });

  it("spans the ramp across the window within an outlier-sized axis", async () => {
    render(Harness);
    await minimumInput().fill("10");
    await maximumInput().fill("70");

    await expect.element(selected("window")).toHaveTextContent("[10,70]");
    await expect.element(minimumSlider()).toHaveAttribute("aria-valuenow", "10");
    await expect.element(maximumSlider()).toHaveAttribute("aria-valuenow", "70");
    await expect.element(maximumSlider()).toHaveAttribute("aria-valuemax", "500");
    expect(gradient()).toContain("2.000%");
    expect(gradient()).toContain("14.000%");
  });

  it("places each window value above its own handle", async () => {
    render(Harness, { initialWindow: [100, 400] });
    const track = elementOf(page.getByTestId("color-focus-track"), HTMLElement).getBoundingClientRect();
    const min = elementOf(minimumInput(), HTMLInputElement).getBoundingClientRect();
    const max = elementOf(maximumInput(), HTMLInputElement).getBoundingClientRect();
    expect(min.bottom).toBeLessThanOrEqual(track.top);
    expect(min.right).toBeCloseTo(track.left + track.width * 0.2, -1);
    expect(max.left).toBeCloseTo(track.left + track.width * 0.8, -1);
    expect(min.right).toBeLessThanOrEqual(max.left);
  });

  it("adds a boundary that pins the color already there, then warps the ramp when moved", async () => {
    render(Harness, { initialWindow: [10, 70] });
    const before = gradient();
    await addBoundary().click();

    await expect.element(boundaryInput(1)).toHaveValue(40);
    await expect.element(selected("boundaries")).toHaveTextContent('[{"value":40,"position":0.5}]');

    await boundaryInput(1).fill("20");
    await expect.element(selected("boundaries")).toHaveTextContent('[{"value":20,"position":0.5}]');
    await expect.element(boundarySlider(1)).toHaveAttribute("aria-valuenow", "20");
    expect(gradient()).not.toBe(before);

    await addBoundary().click();
    await expect.element(boundaryInput(2)).toHaveValue(45);
    expect(page.getByRole("slider").elements()).toHaveLength(4);
  });

  it("leaves an automatic window automatic when a boundary is added, so later data is not dimmed", async () => {
    const screen = render(Harness);
    await addBoundary().click();
    await expect.element(selected("window")).toHaveTextContent("null");
    await expect.element(selected("boundaries")).toHaveTextContent('[{"value":250,"position":0.5}]');
    await screen.rerender({ observedMax: 800 });
    await expect.element(maximumInput()).toHaveValue(800);
    await expect.element(boundaryInput(1)).toHaveValue(250);
  });

  it("disables adding a boundary when none fits", async () => {
    render(Harness, {
      glucose: true,
      metricLabel: "Avg Glucose",
      initialWindow: [100, 102],
      initialBoundaries: [{ value: 101, position: 0.2 }],
    });
    await expect.element(addBoundary()).toBeDisabled();
  });

  it("keeps a boundary between its neighbours, and lets the window move past boundaries", async () => {
    render(Harness, {
      initialWindow: [10, 70],
      initialBoundaries: [
        { value: 20, position: 0.3 },
        { value: 50, position: 0.6 },
      ],
    });
    for (const value of ["", "50", "60", "0"]) {
      await boundaryInput(1).fill(value);
      await expect.element(boundaryInput(1)).toHaveAttribute("aria-invalid", "true");
    }
    await expect
      .element(selected("boundaries"))
      .toHaveTextContent('[{"value":20,"position":0.3},{"value":50,"position":0.6}]');
    await maximumInput().fill("45");
    await expect.element(selected("window")).toHaveTextContent("[10,45]");
    await expect
      .element(selected("boundaries"))
      .toHaveTextContent('[{"value":20,"position":0.3},{"value":50,"position":0.6}]');
    await expect.element(boundarySlider(2)).toHaveAttribute("aria-valuenow", "50");
  });

  it("removes a boundary and clears the list with the last one", async () => {
    render(Harness, { initialWindow: [10, 70], initialBoundaries: [{ value: 20, position: 0.3 }] });
    await page.getByRole("button", { name: "Remove TDD color boundary 1" }).click();
    await expect.element(selected("boundaries")).toHaveTextContent("null");
    expect(page.getByRole("slider").elements()).toHaveLength(2);
  });

  it("moves handles by keyboard without crossing or collapsing the window", async () => {
    render(Harness, { initialWindow: [10, 10.2] });

    elementOf(minimumSlider(), HTMLElement).focus();
    await userEvent.keyboard("{ArrowRight}");
    await expect.element(minimumSlider()).toHaveAttribute("aria-valuenow", "10.1");
    await userEvent.keyboard("{ArrowRight}{End}");
    await expect.element(minimumSlider()).toHaveAttribute("aria-valuenow", "10.1");

    elementOf(maximumSlider(), HTMLElement).focus();
    await userEvent.keyboard("{ArrowLeft}{Home}");
    await expect.element(maximumSlider()).toHaveAttribute("aria-valuenow", "10.2");
    await expect.element(selected("window")).toHaveTextContent("[10.1,10.2]");
  });

  it("drags the upper handle on the color bar at mobile width", async () => {
    const originalSize = [window.innerWidth, window.innerHeight] as const;
    await page.viewport(390, 700);
    try {
      render(Harness, { initialWindow: [10, 70] });
      const track = page.getByTestId("color-focus-track");
      const bounds = elementOf(track, HTMLElement).getBoundingClientRect();
      expect(bounds.width).toBeGreaterThan(300);

      await page.screenshot({ path: "test-results/color-focus-mobile.png" });
      await userEvent.dragAndDrop(maximumSlider(), track, {
        targetPosition: { x: bounds.width * 0.6, y: bounds.height / 2 },
      });

      await expect.element(minimumInput()).toHaveValue(10);
      const maximum = elementOf(maximumInput(), HTMLInputElement).valueAsNumber;
      expect(maximum).toBeGreaterThan(299);
      expect(maximum).toBeLessThan(301);
    } finally {
      await page.viewport(originalSize[0], originalSize[1]);
    }
  });

  it("lays the controls out in one row on a wide container", async () => {
    const originalSize = [window.innerWidth, window.innerHeight] as const;
    await page.viewport(1400, 700);
    try {
      render(Harness);
      const reset = elementOf(page.getByRole("button", { name: "Reset TDD color scale" }), HTMLElement).getBoundingClientRect();
      const track = elementOf(page.getByTestId("color-focus-track"), HTMLElement).getBoundingClientRect();
      expect(reset.left).toBeGreaterThan(track.right);
      await page.screenshot({ path: "test-results/color-focus-wide.png" });
    } finally {
      await page.viewport(originalSize[0], originalSize[1]);
    }
  });

  it.each(["", "-1", "70", "80", "1e309"])(
    "rejects invalid minimum %j without changing the active colors",
    async (value) => {
      render(Harness, { initialWindow: [10, 70] });
      await minimumInput().fill(value);
      await expect.element(minimumInput()).toHaveAttribute("aria-invalid", "true");
      await expect.element(page.getByRole("alert")).toBeVisible();
      await expect.element(selected("window")).toHaveTextContent("[10,70]");
      await expect.element(minimumSlider()).toHaveAttribute("aria-valuenow", "10");
    }
  );

  it("rejects a TIR maximum above 100 percent", async () => {
    render(Harness, { metricLabel: "Time in Range", unit: "%", fixedMax: 100, initialWindow: [10, 70] });
    await maximumInput("Time in Range").fill("101");
    await expect.element(maximumInput("Time in Range")).toHaveAttribute("aria-invalid", "true");
    await expect.element(page.getByRole("alert")).toHaveTextContent("between 0 and 100 %");
    await expect.element(selected("window")).toHaveTextContent("[10,70]");
  });

  it("resets the window and boundaries, clearing an invalid draft", async () => {
    render(Harness, { initialWindow: [10, 70], initialBoundaries: [{ value: 20, position: 0.3 }] });
    await minimumInput().fill("600");
    await expect.element(page.getByRole("alert")).toBeVisible();
    await page.getByRole("button", { name: "Reset TDD color scale" }).click();
    await expect.element(page.getByRole("alert")).not.toBeInTheDocument();
    await expect.element(minimumInput()).toHaveValue(0);
    await expect.element(maximumInput()).toHaveValue(500);
    await expect.element(selected("window")).toHaveTextContent("null");
    await expect.element(selected("boundaries")).toHaveTextContent("null");
  });

  it("edits glucose in mmol/L while storing mg/dL, with the ramp fixed to its own domain", async () => {
    render(Harness, { glucose: true, units: "mmol", metricLabel: "Avg Glucose" });
    await expect.element(minimumInput("Avg Glucose")).toHaveValue(2.2);
    await expect.element(maximumInput("Avg Glucose")).toHaveValue(19.4);
    const before = gradient("glucose-color-track");

    await minimumInput("Avg Glucose").fill("3.9");
    await expect.element(selected("window")).toHaveTextContent("[70,350]");
    expect(gradient("glucose-color-track")).toBe(before);

    await addBoundary().click();
    await expect.element(boundaryInput(1, "Avg Glucose")).toHaveValue(11.7);
    await expect.element(selected("window")).toHaveTextContent("[70,350]");
    await expect.element(boundaryInput(1, "Avg Glucose")).toHaveAttribute("step", "0.1");
  });

  it("colors a boundary and an end from the picker", async () => {
    render(Harness);
    await addBoundary().click();
    pick("TDD color boundary 1 color", "#ff0000");
    await expect.element(selected("boundaries")).toHaveTextContent('"color":"#ff0000"');
    expect(gradient()).toContain("#ff0000");

    pick("TDD high end color", "#00ff00");
    await expect.element(selected("ends")).toHaveTextContent('{"high":"#00ff00"}');
    expect(gradient()).toContain("#00ff00");
  });

  it("swaps the end colors of a dose scale", async () => {
    render(Harness);
    pick("TDD low end color", "#000000");
    pick("TDD high end color", "#ffffff");
    await page.getByRole("button", { name: "Swap TDD end colors" }).click();
    await expect.element(selected("ends")).toHaveTextContent('{"low":"#ffffff","high":"#000000"}');
  });

  it("offers no swap on the glucose ramp", async () => {
    render(Harness, { glucose: true, metricLabel: "Avg Glucose" });
    await expect.element(minimumInput("Avg Glucose")).toBeInTheDocument();
    expect(page.getByRole("button", { name: /Swap/ }).elements()).toHaveLength(0);
  });

  it("clears chosen colors on reset", async () => {
    render(Harness);
    pick("TDD low end color", "#123456");
    await expect.element(selected("ends")).toHaveTextContent("#123456");
    await page.getByRole("button", { name: "Reset TDD color scale" }).click();
    await expect.element(selected("ends")).toHaveTextContent("{}");
  });
});
