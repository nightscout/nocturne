import { afterEach, describe, expect, it } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import Harness from "./Headroom.test-harness.svelte";

const panel = () => page.getByTestId("panel").element().getBoundingClientRect();
/** The harness sets --headroom-top to this. */
const pinnedTop = 56;

async function scrollTo(y: number) {
  window.scrollTo(0, y);
  window.dispatchEvent(new Event("scroll"));
  await new Promise(requestAnimationFrame);
}

describe("Headroom", () => {
  afterEach(() => window.scrollTo(0, 0));

  it("scrolls away with the page, slides back in on the way up, and settles into the flow at the top", async () => {
    render(Harness);
    const start = panel().top;

    await scrollTo(40);
    expect(panel().top).toBeCloseTo(start - 40, 0);

    // Fully hidden clears the header too, which may be translucent.
    await scrollTo(1000);
    expect(panel().bottom).toBeLessThanOrEqual(0);

    await scrollTo(970);
    expect(panel().bottom).toBeCloseTo(30, 0);
    // Stopped partway up, it finishes coming back.
    await expect.poll(() => panel().top, { timeout: 1000 }).toBeCloseTo(pinnedTop, 0);

    await scrollTo(1010);
    expect(panel().top).toBeCloseTo(pinnedTop - 40, 0);
    // Stopped partway down, it finishes leaving, however little it had moved.
    await expect.poll(() => panel().bottom, { timeout: 1000 }).toBeLessThanOrEqual(pinnedTop + 0.5);

    await scrollTo(0);
    expect(panel().top).toBeCloseTo(start, 0);
  });

  it("reveals itself when focus moves into it", async () => {
    render(Harness);
    await scrollTo(1000);
    expect(panel().bottom).toBeLessThanOrEqual(pinnedTop);
    page.getByTestId("panel").element().dispatchEvent(new FocusEvent("focusin", { bubbles: true }));
    await expect.poll(() => panel().top, { timeout: 1000 }).toBeCloseTo(pinnedTop, 0);
  });
});
