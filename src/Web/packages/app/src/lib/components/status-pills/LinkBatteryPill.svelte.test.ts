import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import LinkBatteryPill from "./LinkBatteryPill.svelte";

describe("LinkBatteryPill", () => {
  it("renders the link battery voltage to two decimals with a V suffix", () => {
    const { container } = render(LinkBatteryPill, { voltage: 2.934 });
    expect(container.textContent).toContain("Link");
    expect(container.textContent).toContain("2.93 V");
  });
});
