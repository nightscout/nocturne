import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { HistorySentinel } from "@nocturne/coach";
import type { CoachNavigation, CoachRouter, SentinelWindow } from "@nocturne/coach";

interface Entry {
  url: string;
  coachMark: boolean;
}

/**
 * A browser and a router with shallow routing. `back()` lands a task later, as a browser's does,
 * and the sentinel's popstate listener runs before the router updates its page state, as it does on
 * a first load.
 */
function fakeBrowser() {
  const entries: Entry[] = [{ url: "/a", coachMark: false }];
  let index = 0;
  let pageState: Entry = entries[0];
  const listeners = new Set<() => void>();
  let hook: (navigation: CoachNavigation) => void = () => {};

  const history = {
    pushState: vi.fn(),
    replaceState: vi.fn(),
    back() {
      setTimeout(() => {
        index--;
        for (const listener of listeners) listener();
        pageState = entries[index];
      }, 0);
    },
  };

  const win = {
    history,
    addEventListener: (_: string, listener: () => void) => listeners.add(listener),
    removeEventListener: (_: string, listener: () => void) => listeners.delete(listener),
    setTimeout: (fn: () => void, ms: number) => setTimeout(fn, ms),
  } as unknown as SentinelWindow;

  function commit(entry: Entry) {
    entries.splice(index + 1);
    entries.push(entry);
    index++;
    pageState = entry;
  }

  function navigate(type: string, url = "/next") {
    let settle: () => void = () => {};
    let fail: (reason: unknown) => void = () => {};
    const complete = new Promise<void>((resolve, reject) => {
      settle = resolve;
      fail = reject;
    });
    const navigation = {
      type,
      to: { url: new URL(url, "https://example.test") },
      willUnload: false,
      cancelled: false,
      cancel() {
        navigation.cancelled = true;
        fail(new Error("cancelled"));
      },
      complete,
    };
    hook(navigation);
    return {
      navigation,
      finish: () => {
        if (navigation.cancelled) return;
        commit({ url, coachMark: false });
        settle();
      },
    };
  }

  const router: CoachRouter = {
    beforeNavigate: (callback) => {
      hook = callback;
    },
    goto: vi.fn(async (target: URL) => navigate("goto", target.pathname).finish()),
    pushMarkedEntry: () => commit({ url: entries[index].url, coachMark: true }),
    isMarkedEntry: () => pageState.coachMark,
  };

  return {
    win,
    history,
    router,
    navigate,
    pressBack: () => history.back(),
    get current() {
      return entries[index];
    },
    /** The entries a back press can reach, oldest first, ending at the current one. */
    get reachable() {
      return entries.slice(0, index + 1);
    },
  };
}

function connected(onBack: () => void = () => {}) {
  const browser = fakeBrowser();
  const sentinel = new HistorySentinel(onBack, browser.win);
  sentinel.bindRouter(browser.router);
  sentinel.connect();
  return { browser, sentinel };
}

const flush = () => vi.runAllTimersAsync();

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

describe("history sentinel", () => {
  it("adds a shallow entry through the router, never through the history API", () => {
    const { browser, sentinel } = connected();

    sentinel.push();

    expect(browser.current).toEqual({ url: "/a", coachMark: true });
    expect(browser.history.pushState).not.toHaveBeenCalled();
    expect(browser.history.replaceState).not.toHaveBeenCalled();
  });

  it("dismisses the overlay on back and stays on the page", async () => {
    const onBack = vi.fn();
    const { browser, sentinel } = connected(onBack);
    sentinel.push();

    browser.pressBack();
    await flush();
    sentinel.release();
    await flush();

    expect(onBack).toHaveBeenCalledOnce();
    expect(browser.current).toEqual({ url: "/a", coachMark: false });
  });

  it("pops its entry when the overlay closes another way", async () => {
    const onBack = vi.fn();
    const { browser, sentinel } = connected(onBack);
    sentinel.push();

    sentinel.release();
    await flush();

    expect(browser.current).toEqual({ url: "/a", coachMark: false });
    expect(onBack).not.toHaveBeenCalled();
  });

  it("keeps one entry while the overlay moves from step to step", async () => {
    const { browser, sentinel } = connected();

    sentinel.push();
    sentinel.release();
    sentinel.push();
    await flush();

    expect(browser.current.coachMark).toBe(true);
    sentinel.release();
    await flush();
    expect(browser.current).toEqual({ url: "/a", coachMark: false });
  });

  it("re-issues a link followed from the overlay without leaving its entry under the new page", async () => {
    const { browser, sentinel } = connected();
    sentinel.push();

    const { navigation } = browser.navigate("link", "/reports");
    expect(navigation.cancelled).toBe(true);
    await flush();

    expect(browser.router.goto).toHaveBeenCalledWith(new URL("https://example.test/reports"));
    expect(browser.reachable).toEqual([
      { url: "/a", coachMark: false },
      { url: "/reports", coachMark: false },
      { url: "/reports", coachMark: true },
    ]);

    sentinel.release();
    await flush();
    expect(browser.current).toEqual({ url: "/reports", coachMark: false });
  });

  it("adopts an entry already marked rather than pushing a second one", async () => {
    const onBack = vi.fn();
    const { browser, sentinel } = connected(onBack);
    browser.router.pushMarkedEntry();

    sentinel.push();
    expect(browser.reachable).toHaveLength(2);

    browser.pressBack();
    await flush();
    expect(onBack).toHaveBeenCalledOnce();
  });

  it("lets a link through when its entry is no longer current", async () => {
    const { browser, sentinel } = connected();
    sentinel.push();
    const back = vi.spyOn(browser.history, "back");

    browser.navigate("goto", "/later");
    browser.navigate("goto", "/next").finish();
    await flush();
    const { navigation } = browser.navigate("link", "/reports");
    await flush();

    expect(navigation.cancelled).toBe(false);
    expect(back).not.toHaveBeenCalled();
  });

  it("does not pop under a navigation in flight, which would cancel it", async () => {
    const { browser, sentinel } = connected();
    sentinel.push();
    const back = vi.spyOn(browser.history, "back");

    const { finish } = browser.navigate("goto");
    sentinel.release();
    await flush();
    expect(back).not.toHaveBeenCalled();

    finish();
    await flush();
    expect(browser.current).toEqual({ url: "/next", coachMark: false });
  });

  it("defers a push during a navigation in flight until it settles", async () => {
    const { browser, sentinel } = connected();

    const { finish } = browser.navigate("goto");
    sentinel.push();
    expect(browser.current).toEqual({ url: "/a", coachMark: false });

    finish();
    await flush();
    expect(browser.current).toEqual({ url: "/next", coachMark: true });
  });

  it("pops its entry once a navigation that never left it is cancelled", async () => {
    const { browser, sentinel } = connected();
    sentinel.push();

    const { navigation } = browser.navigate("goto");
    sentinel.release();
    navigation.cancel();
    await flush();

    expect(browser.current).toEqual({ url: "/a", coachMark: false });
  });
});
