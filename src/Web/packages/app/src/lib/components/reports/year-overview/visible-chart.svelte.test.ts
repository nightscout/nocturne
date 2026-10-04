import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { PRINT_LAYOUT_CLASS } from "$lib/components/charts/print/print-mode.svelte";
import Harness from "./VisibleChart.test-harness.svelte";

let intersect = (_visible: boolean) => {};
const disconnect = vi.fn();

class Observer implements IntersectionObserver {
  root = null;
  rootMargin = "100px";
  scrollMargin = "0px";
  thresholds = [0];
  disconnect = disconnect;
  unobserve = vi.fn();
  takeRecords = () => [];
  constructor(private callback: IntersectionObserverCallback) {}
  observe(target: Element) {
    intersect = (isIntersecting) => {
      const bounds = target.getBoundingClientRect();
      this.callback(
        [
          {
            target,
            isIntersecting,
            intersectionRatio: isIntersecting ? 1 : 0,
            boundingClientRect: bounds,
            intersectionRect: bounds,
            rootBounds: null,
            time: performance.now(),
          },
        ],
        this
      );
    };
  }
}

describe("chart visibility", () => {
  beforeEach(() => {
    disconnect.mockClear();
    vi.stubGlobal("IntersectionObserver", Observer);
  });
  afterEach(() => {
    document.documentElement.classList.remove(PRINT_LAYOUT_CLASS);
    vi.unstubAllGlobals();
  });

  it("renders the newest chart without waiting for viewport observation", async () => {
    render(Harness, { eager: true });
    await expect.element(page.getByTestId("chart")).toBeInTheDocument();
  });

  it("defers creation and uses the latest data when entering the viewport", async () => {
    render(Harness);
    await expect.element(page.getByTestId("chart")).not.toBeInTheDocument();
    await page.getByRole("button", { name: "Update data" }).click();
    intersect(true);
    await expect
      .element(page.getByTestId("chart"))
      .toHaveTextContent("Updated data");
    expect(disconnect).toHaveBeenCalled();
    const chart = page.getByTestId("chart").element();
    intersect(false);
    expect(page.getByTestId("chart").element()).toBe(chart);
  });

  it("renders offscreen content for prepared and native printing", async () => {
    render(Harness);
    document.documentElement.classList.add(PRINT_LAYOUT_CLASS);
    await expect.element(page.getByTestId("chart")).toBeInTheDocument();
    document.documentElement.classList.remove(PRINT_LAYOUT_CLASS);
    await expect.element(page.getByTestId("chart")).not.toBeInTheDocument();
    window.dispatchEvent(new Event("beforeprint"));
    await expect.element(page.getByTestId("chart")).toBeInTheDocument();
    window.dispatchEvent(new Event("afterprint"));
    await expect.element(page.getByTestId("chart")).not.toBeInTheDocument();
  });

  it("lets keyboard users reach an offscreen chart without viewport observation", async () => {
    render(Harness);
    await expect.element(page.getByTestId("chart")).not.toBeInTheDocument();
    page.getByRole("region", { name: "Year chart" }).element().focus();
    await expect.element(page.getByTestId("chart")).toBeInTheDocument();
    await expect
      .element(page.getByRole("region", { name: "Year chart" }))
      .toHaveFocus();
  });

  it("disconnects when disposed before becoming visible", async () => {
    const component = render(Harness);
    await component.unmount();
    expect(disconnect).toHaveBeenCalled();
  });

  it("renders immediately when viewport observation is unavailable", async () => {
    vi.stubGlobal("IntersectionObserver", undefined);
    render(Harness);
    await expect.element(page.getByTestId("chart")).toBeInTheDocument();
  });
});
