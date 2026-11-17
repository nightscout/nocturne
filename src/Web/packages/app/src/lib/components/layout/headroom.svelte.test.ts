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

  it("scrolls away with the page and settles into the flow at the top", async () => {
    render(Harness);
    const start = panel().top;

    await scrollTo(40);
    expect(panel().top).toBeCloseTo(start - 40, 0);

    // Fully hidden clears the header too, which may be translucent.
    await scrollTo(1000);
    expect(panel().bottom).toBeLessThanOrEqual(0);

    await scrollTo(0);
    await expect.poll(() => panel().top, { timeout: 1000 }).toBeCloseTo(start, 0);
  });

  it("stays hidden on a small scroll up and slides in once the page has gone up 50px", async () => {
    render(Harness);
    await scrollTo(1000);

    await scrollTo(980);
    await scrollTo(970);
    expect(panel().bottom).toBeLessThanOrEqual(0);

    await scrollTo(950);
    await expect.poll(() => panel().top, { timeout: 1000 }).toBeCloseTo(pinnedTop, 0);

    // Scrolling on up keeps it pinned.
    await scrollTo(800);
    expect(panel().top).toBeCloseTo(pinnedTop, 0);
  });

  it("starts counting again after any scroll down", async () => {
    render(Harness);
    await scrollTo(1000);
    await scrollTo(960);
    await scrollTo(970);
    await scrollTo(930);
    expect(panel().bottom).toBeLessThanOrEqual(0);
    await scrollTo(920);
    await expect.poll(() => panel().top, { timeout: 1000 }).toBeCloseTo(pinnedTop, 0);
  });

  it("slides away on the next scroll down", async () => {
    render(Harness);
    await scrollTo(1000);
    await scrollTo(900);
    await expect.poll(() => panel().top, { timeout: 1000 }).toBeCloseTo(pinnedTop, 0);
    await scrollTo(910);
    await expect.poll(() => panel().bottom, { timeout: 1000 }).toBeLessThanOrEqual(0);
  });

  it("reveals itself when focus moves into it", async () => {
    render(Harness);
    await scrollTo(1000);
    expect(panel().bottom).toBeLessThanOrEqual(pinnedTop);
    page.getByTestId("panel").element().dispatchEvent(new FocusEvent("focusin", { bubbles: true }));
    await expect.poll(() => panel().top, { timeout: 1000 }).toBeCloseTo(pinnedTop, 0);
  });
});
