import { afterEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import type { CoachRouter } from "@nocturne/coach";
import Harness from "./OverlayContinuityHarness.test.svelte";

/**
 * A router with SvelteKit's shallow routing: its page state is reactive, as `$app/state`'s is, and
 * a back traversal lands a task later. The sentinel's `history.back()` is routed here so it stays
 * on the test page.
 */
function shallowRouter() {
  const entries: Record<string, unknown>[] = [{}];
  let index = 0;
  let state = $state.raw<Record<string, unknown>>({});

  function traverseBack() {
    setTimeout(() => {
      index--;
      window.dispatchEvent(new PopStateEvent("popstate"));
      state = entries[index];
    }, 0);
  }

  const sentinelBacks = vi.spyOn(window.history, "back").mockImplementation(traverseBack);
  const pushes = vi.fn(() => {
    entries.splice(index + 1);
    entries.push({ ...state, coachMark: true });
    index++;
    state = entries[index];
  });

  const router: CoachRouter = {
    beforeNavigate: () => {},
    goto: async () => {},
    pushMarkedEntry: pushes,
    isMarkedEntry: () => state.coachMark === true,
  };

  return {
    router,
    pushes,
    sentinelBacks,
    pressBack: traverseBack,
    get depth() {
      return index;
    },
  };
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("coach mark history entry", () => {
  it("holds one entry over a whole tour and lets back dismiss it without a second pop", async () => {
    const browser = shallowRouter();
    render(Harness, { props: { router: browser.router } });

    await expect.element(page.getByText("First stop.")).toBeVisible();
    await page.getByRole("button", { name: "Done", exact: true }).click();
    await expect.element(page.getByText("Second stop.")).toBeVisible();
    expect(browser.pushes).toHaveBeenCalledOnce();

    browser.pressBack();
    await expect.element(page.getByText("Second stop.")).not.toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(browser.sentinelBacks).not.toHaveBeenCalled();
    expect(browser.pushes).toHaveBeenCalledOnce();
    expect(browser.depth).toBe(0);
  });

  it("pops its entry once when the overlay is closed with Escape", async () => {
    const browser = shallowRouter();
    render(Harness, { props: { router: browser.router } });

    await expect.element(page.getByText("First stop.")).toBeVisible();
    await userEvent.keyboard("{Escape}");
    await expect.element(page.getByText("First stop.")).not.toBeInTheDocument();
    await vi.waitFor(() => expect(browser.depth).toBe(0));
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(browser.sentinelBacks).toHaveBeenCalledOnce();
    expect(browser.pushes).toHaveBeenCalledOnce();
  });
});
