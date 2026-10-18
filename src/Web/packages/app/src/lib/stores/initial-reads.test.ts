import { afterEach, describe, expect, it, vi } from "vitest";
import {
  initialReadWindows,
  initialReadsClock,
  releaseInitialReads,
  takeInitialRead,
  type InitialReads,
} from "./initial-reads";

const NOW = Date.parse("2026-10-10T12:00:00.000Z");

function stash(now: number, urls: string[] = []): InitialReads {
  return {
    now,
    responses: new Map(urls.map((u) => [u, Promise.resolve(new Response(u))])),
  };
}

describe("initialReadsClock", () => {
  it("is the current clock when nothing was prefetched", () => {
    expect(initialReadsClock(undefined, NOW)).toBe(NOW);
  });

  it("is the prefetch clock while the stash is under 15 seconds old", () => {
    expect(initialReadsClock(stash(NOW - 14_999), NOW)).toBe(NOW - 14_999);
  });

  it("is the current clock once the stash is 15 seconds old", () => {
    expect(initialReadsClock(stash(NOW - 15_000), NOW)).toBe(NOW);
  });

  it("is the current clock when the stash claims to be from the future", () => {
    expect(initialReadsClock(stash(NOW + 1), NOW)).toBe(NOW);
  });
});

describe("initialReadWindows", () => {
  it("spans the day before the reference time", () => {
    const w = initialReadWindows(NOW);
    expect(w.to).toBe("2026-10-10T12:00:00.000Z");
    expect(w.oneDayAgo).toBe("2026-10-09T12:00:00.000Z");
    expect(w.glucoseFrom).toBe(w.oneDayAgo);
  });
});

describe("takeInitialRead", () => {
  const url = "/api/v4/notifications";

  it("hands over a prefetched GET once", () => {
    const s = stash(NOW, [url]);
    expect(takeInitialRead(s, url, undefined)).toBeDefined();
    expect(takeInitialRead(s, url, "GET")).toBeUndefined();
  });

  it("matches the exact URL only", () => {
    const s = stash(NOW, [url]);
    expect(takeInitialRead(s, `${url}?x=1`, "GET")).toBeUndefined();
    expect(s.responses.size).toBe(1);
  });

  it("leaves the stash for a non-GET request", () => {
    const s = stash(NOW, [url]);
    expect(takeInitialRead(s, url, "POST")).toBeUndefined();
    expect(s.responses.size).toBe(1);
  });

  it("ignores a request that is not a URL string", () => {
    const s = stash(NOW, [url]);
    expect(takeInitialRead(s, new Request("http://localhost" + url), "GET")).toBeUndefined();
  });

  it("returns nothing without a stash", () => {
    expect(takeInitialRead(undefined, url, "GET")).toBeUndefined();
  });
});

describe("releaseInitialReads", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("cancels responses nothing took and removes the stash", async () => {
    const cancel = vi.fn();
    const body = new ReadableStream({ cancel });
    const parked: InitialReads = {
      now: NOW,
      responses: new Map([["/left-over", Promise.resolve(new Response(body))]]),
    };
    const win: { __nocturneInitialReads?: InitialReads } = { __nocturneInitialReads: parked };
    vi.stubGlobal("window", win);

    releaseInitialReads();
    await vi.waitFor(() => expect(cancel).toHaveBeenCalledOnce());

    expect(win.__nocturneInitialReads).toBeUndefined();
  });

  it("releases a stash that was fully consumed", () => {
    const parked = stash(NOW, ["/a"]);
    const win: { __nocturneInitialReads?: InitialReads } = { __nocturneInitialReads: parked };
    vi.stubGlobal("window", win);
    takeInitialRead(parked, "/a", "GET");

    expect(() => releaseInitialReads()).not.toThrow();
    expect(win.__nocturneInitialReads).toBeUndefined();
  });

  it("does nothing without a stash", () => {
    vi.stubGlobal("window", {});
    expect(() => releaseInitialReads()).not.toThrow();
  });
});
