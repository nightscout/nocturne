import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import Harness from "./DeviceEventMarkerHarness.test.svelte";

/** An inferred sensor start reads apart from a recorded one at a glance: its ring is dashed. */
describe("DeviceEventMarker", () => {
  it("draws an inferred event's ring dashed", async () => {
    const { container } = render(Harness, { isInferred: true });

    expect(container.querySelector("circle")?.getAttribute("stroke-dasharray")).toBeTruthy();
    expect(container.querySelector("g[data-inferred]")).not.toBeNull();
  });

  it("draws a recorded event's ring solid", async () => {
    const { container } = render(Harness, {});

    expect(container.querySelector("circle")?.hasAttribute("stroke-dasharray")).toBe(false);
    expect(container.querySelector("g[data-inferred]")).toBeNull();
  });
});
