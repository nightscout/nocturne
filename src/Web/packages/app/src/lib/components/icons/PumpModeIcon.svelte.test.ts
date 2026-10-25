import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import { PumpModeState } from "$api";
import PumpModeIcon from "./PumpModeIcon.svelte";

function iconClass(state: string): string {
  const { container } = render(PumpModeIcon, { state });
  return container.querySelector("svg")?.getAttribute("class") ?? "";
}

describe("PumpModeIcon", () => {
  it("draws the ease off icon for PumpModeState.EaseOff", () => {
    expect(iconClass(PumpModeState.EaseOff)).toContain("trending-down");
  });

  it("draws a different icon for each of two other modes", () => {
    const boost = iconClass(PumpModeState.Boost);
    const sleep = iconClass(PumpModeState.Sleep);
    expect(boost).not.toContain("trending-down");
    expect(sleep).not.toContain("trending-down");
    expect(boost).not.toBe(sleep);
  });
});
