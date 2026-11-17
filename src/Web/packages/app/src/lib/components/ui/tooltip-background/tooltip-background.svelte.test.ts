import { render } from "vitest-browser-svelte";
import { afterEach, describe, expect, it, vi } from "vitest";
import TooltipHarness from "./TooltipHarness.test.svelte";

const themes = ["nocturne", "trio-theme", "aaps-theme", "classic-theme"];
const modes = ["light", "dark"];
const root = document.documentElement;

function showTooltip(chart: Element) {
  const rect = chart.getBoundingClientRect();
  const init = {
    bubbles: true,
    clientX: rect.x + rect.width / 2,
    clientY: rect.y + rect.height / 2,
  };
  chart.dispatchEvent(new PointerEvent("pointerenter", init));
  chart.dispatchEvent(new PointerEvent("pointermove", init));
}

function alpha(color: string) {
  const probe = document.createElement("canvas").getContext("2d")!;
  probe.clearRect(0, 0, 1, 1);
  probe.fillStyle = color;
  probe.fillRect(0, 0, 1, 1);
  return probe.getImageData(0, 0, 1, 1).data[3] / 255;
}

afterEach(() => {
  root.classList.remove("dark", ...themes);
  document.body.querySelectorAll(".lc-tooltip-root").forEach((el) => el.remove());
});

describe("layerchart tooltip background", () => {
  describe.each(themes)("%s", (theme) => {
    it.each(modes)("is opaque in %s mode", async (mode) => {
      root.classList.add(theme);
      root.classList.toggle("dark", mode === "dark");
      const screen = render(TooltipHarness);

      let background = "";
      await vi.waitFor(() => {
        showTooltip(screen.getByTestId("hit-area").element());
        const tooltip = document.body.querySelector(".lc-tooltip-container");
        expect(tooltip).not.toBeNull();
        background = getComputedStyle(tooltip!).backgroundColor;
      });

      expect(background).not.toBe("");
      expect(alpha(background)).toBe(1);
    });
  });
});
