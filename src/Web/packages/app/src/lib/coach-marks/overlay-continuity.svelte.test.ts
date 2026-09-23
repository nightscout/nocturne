import { beforeEach, describe, expect, it } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import Harness from "./OverlayContinuityHarness.test.svelte";

const BACKDROP_CLASS = "coach-backdrop";

interface BackdropWatch {
  removals: number;
  additions: number;
  stop: () => void;
}

/** Counts backdrop mounts and unmounts over a step transition. */
function watchBackdrop(): BackdropWatch {
  const watch: BackdropWatch = { removals: 0, additions: 0, stop: () => {} };

  const isBackdrop = (node: Node) =>
    node instanceof HTMLElement && node.classList.contains(BACKDROP_CLASS);

  const observer = new MutationObserver((records) => {
    for (const record of records) {
      for (const node of record.removedNodes) if (isBackdrop(node)) watch.removals++;
      for (const node of record.addedNodes) if (isBackdrop(node)) watch.additions++;
    }
  });
  observer.observe(document.body, { childList: true, subtree: true });

  watch.stop = () => observer.disconnect();

  return watch;
}

function expectUninterrupted(watch: BackdropWatch, backdrop: Element): void {
  watch.stop();
  expect({
    removals: watch.removals,
    additions: watch.additions,
    sameNode: page.getByTestId("coach-backdrop").element() === backdrop,
    connected: backdrop.isConnected,
  }).toEqual({
    removals: 0,
    additions: 0,
    sameNode: true,
    connected: true,
  });
}

describe("coach mark overlay continuity", () => {
  // Unmounting the overlay pops its sentinel history entry, and the popstate
  // that follows is a task of its own. Let the previous test's land before
  // rendering, or the next provider sees a still-marked history entry and pops
  // a second time — which in the runner's iframe walks off the test page.
  beforeEach(async () => {
    await new Promise((resolve) => setTimeout(resolve, 200));
  });

  it("keeps the same backdrop across an organic sequence advance", async () => {
    render(Harness);

    await expect.element(page.getByText("First stop.")).toBeVisible();
    const backdrop = page.getByTestId("coach-backdrop").element();
    const watch = watchBackdrop();

    await page.getByRole("button", { name: "Got it" }).click();
    await expect.element(page.getByText("Second stop.")).toBeVisible();
    await page.getByRole("button", { name: "Got it" }).click();
    await expect.element(page.getByText("Third stop.")).toBeVisible();

    expectUninterrupted(watch, backdrop);
  });

  it("keeps the same backdrop across a forced sequence advance", async () => {
    render(Harness, { props: { forced: true } });

    await page.getByRole("button", { name: "Start tour" }).click();
    await expect.element(page.getByText("First stop.")).toBeVisible();
    const backdrop = page.getByTestId("coach-backdrop").element();
    const watch = watchBackdrop();

    await page.getByRole("button", { name: "Got it" }).click();
    await expect.element(page.getByText("Second stop.")).toBeVisible();
    await page.getByRole("button", { name: "Got it" }).click();
    await expect.element(page.getByText("Third stop.")).toBeVisible();

    expectUninterrupted(watch, backdrop);
  });

  it("keeps the same backdrop when leaving a mark on a later local step", async () => {
    render(Harness, { props: { multiStep: true } });

    await expect.element(page.getByText("First stop.")).toBeVisible();
    const backdrop = page.getByTestId("coach-backdrop").element();

    await page.getByRole("button", { name: "Next" }).click();
    await expect.element(page.getByText("First stop, part two.")).toBeVisible();
    const watch = watchBackdrop();

    await page.getByRole("button", { name: "Got it" }).click();
    await expect.element(page.getByText("Second stop.")).toBeVisible();

    expectUninterrupted(watch, backdrop);
  });
});
