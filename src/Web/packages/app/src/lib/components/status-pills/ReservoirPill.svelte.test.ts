import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import ReservoirPill from "./ReservoirPill.svelte";

describe("ReservoirPill", () => {
  it("renders reservoir units to one decimal with a U suffix", () => {
    const { container } = render(ReservoirPill, { reservoir: 42.34 });
    expect(container.textContent).toContain("Reservoir");
    expect(container.textContent).toContain("42.3 U");
  });
});
