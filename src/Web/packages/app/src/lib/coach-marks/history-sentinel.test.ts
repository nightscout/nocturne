import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { HistorySentinel } from "@nocturne/coach";
import type { CoachNavigation, CoachRouter, SentinelWindow } from "@nocturne/coach";

/** A session history whose `back()` lands a task later, as a browser's does. */
function fakeWindow(initialState: unknown = { page: "a" }) {
  const entries: unknown[] = [initialState];
  let index = 0;
  const listeners = new Set<() => void>();

  const history = {
    get state() {
      return entries[index];
    },
    get length() {
      return entries.length;
    },
    pushState(state: unknown) {
      entries.splice(index + 1);
      entries.push(state);
      index++;
    },
    back() {
      setTimeout(() => {
        index--;
        for (const listener of listeners) listener();
      }, 0);
    },
  };

  const win = {
    history,
    addEventListener: (_: string, listener: () => void) => listeners.add(listener),
    removeEventListener: (_: string, listener: () => void) => listeners.delete(listener),
    setTimeout: (fn: () => void, ms: number) => setTimeout(fn, ms),
  } as unknown as SentinelWindow;

  return {
    win,
    history,
    entries,
    get index() {
      return index;
    },
    /** The browser's back button. */
    pressBack: () => history.back(),
    /** A router committing a navigation. */
    navigateTo: (state: unknown) => history.pushState(state),
  };
}

function fakeRouter() {
  let hook: (navigation: CoachNavigation) => void = () => {};
  const router: CoachRouter & { goto: ReturnType<typeof vi.fn> } = {
    beforeNavigate: (callback) => {
      hook = callback;
    },
    goto: vi.fn(async () => {}),
  };

  function navigate(type: string, url = "https://example.test/next") {
    let settle: () => void = () => {};
    let fail: (reason: unknown) => void = () => {};
    const complete = new Promise<void>((resolve, reject) => {
      settle = resolve;
      fail = reject;
    });
    const navigation = {
      type,
      to: { url: new URL(url) },
      willUnload: false,
      cancelled: false,
      cancel() {
        navigation.cancelled = true;
        fail(new Error("cancelled"));
      },
      complete,
    };
    hook(navigation);
    return { navigation, finish: () => settle() };
  }

  return { router, navigate };
}

const flush = () => vi.runAllTimersAsync();

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

describe("history sentinel", () => {
  it("pops its entry when the overlay closes", async () => {
    const browser = fakeWindow();
    const sentinel = new HistorySentinel(() => {}, browser.win);
    sentinel.connect();

    sentinel.push();
    expect(browser.index).toBe(1);
    sentinel.release();
    await flush();

    expect(browser.index).toBe(0);
  });

  it("reports the back button and does not pop a second time", async () => {
    const browser = fakeWindow();
    const onBack = vi.fn();
    const sentinel = new HistorySentinel(onBack, browser.win);
    sentinel.connect();
    sentinel.push();

    browser.pressBack();
    await flush();
    sentinel.release();
    await flush();

    expect(onBack).toHaveBeenCalledOnce();
    expect(browser.index).toBe(0);
  });

  it("does not pop under a navigation in flight, which would cancel it", async () => {
    const browser = fakeWindow();
    const { router, navigate } = fakeRouter();
    const sentinel = new HistorySentinel(() => {}, browser.win);
    sentinel.bindRouter(router);
    sentinel.connect();
    sentinel.push();

    const back = vi.spyOn(browser.history, "back");
    navigate("goto");
    sentinel.release();
    await flush();

    expect(back).not.toHaveBeenCalled();
  });

  it("pops normally once an earlier navigation has finished", async () => {
    const browser = fakeWindow();
    const { router, navigate } = fakeRouter();
    const sentinel = new HistorySentinel(() => {}, browser.win);
    sentinel.bindRouter(router);
    sentinel.connect();

    const { finish } = navigate("goto");
    browser.navigateTo({ page: "b" });
    finish();
    await flush();

    sentinel.push();
    sentinel.release();
    await flush();

    expect(browser.index).toBe(1);
    expect(browser.history.state).toEqual({ page: "b" });
  });

  it("re-issues a link followed from the overlay after popping its entry", async () => {
    const browser = fakeWindow();
    const { router, navigate } = fakeRouter();
    const sentinel = new HistorySentinel(() => {}, browser.win);
    sentinel.bindRouter(router);
    sentinel.connect();
    sentinel.push();

    const { navigation } = navigate("link", "https://example.test/reports");
    expect(navigation.cancelled).toBe(true);
    expect(router.goto).not.toHaveBeenCalled();
    await flush();

    expect(browser.index).toBe(0);
    expect(router.goto).toHaveBeenCalledWith(new URL("https://example.test/reports"));
  });

  it("pops an entry left current by a reload", async () => {
    const browser = fakeWindow({ page: "a", __coachMark: true });
    const sentinel = new HistorySentinel(() => {}, browser.win);
    const back = vi.spyOn(browser.history, "back");

    sentinel.connect();
    await flush();

    expect(back).toHaveBeenCalledOnce();
  });

  it("pushes again when a new overlay rises while the last entry is still popping", async () => {
    const browser = fakeWindow();
    const sentinel = new HistorySentinel(() => {}, browser.win);
    sentinel.connect();

    sentinel.push();
    sentinel.release();
    sentinel.push();
    await flush();

    expect(browser.index).toBe(1);
    expect(browser.history.state).toMatchObject({ __coachMark: true });
  });
});
