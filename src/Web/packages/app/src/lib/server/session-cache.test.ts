import { afterEach, describe, expect, it, vi } from "vitest";
import { clearSessionCache, forgetSession, getCachedSession, sessionCacheKey, type SessionLoad } from "./session-cache";

const signedIn = (name = "a") => ({ isAuthenticated: true, subjectId: "s", name });
const loadOf = (session: object, cacheable = true) =>
  vi.fn(async (): Promise<SessionLoad> => ({ session, cacheable }));

describe("session cache", () => {
  afterEach(() => clearSessionCache());

  it("reuses an authenticated answer for the same credentials within the window", async () => {
    let t = 1_000;
    const load = loadOf(signedIn());

    await getCachedSession("k", load, { now: () => t });
    t += 10_000;
    const again = await getCachedSession("k", load, { now: () => t });

    expect(load).toHaveBeenCalledTimes(1);
    expect(again.name).toBe("a");
  });

  it("asks again once the window has passed", async () => {
    let t = 1_000;
    const load = loadOf(signedIn());

    await getCachedSession("k", load, { now: () => t });
    t += 16_000;
    await getCachedSession("k", load, { now: () => t });

    expect(load).toHaveBeenCalledTimes(2);
  });

  it("shares one call between concurrent checks", async () => {
    let release!: (v: SessionLoad) => void;
    const load = vi.fn(() => new Promise<SessionLoad>((r) => (release = r)));

    const both = Promise.all([getCachedSession("k", load), getCachedSession("k", load)]);
    release({ session: signedIn(), cacheable: true });
    const [a, b] = await both;

    expect(load).toHaveBeenCalledTimes(1);
    expect(a).not.toBe(b);
  });

  it("does not keep or share an answer whose response rotated the tokens", async () => {
    let release!: (v: SessionLoad) => void;
    const first = vi.fn(() => new Promise<SessionLoad>((r) => (release = r)));
    const second = loadOf(signedIn("own"));

    const a = getCachedSession("k", first);
    const b = getCachedSession("k", second);
    release({ session: signedIn("rotated"), cacheable: false });
    await a;

    expect((await b).name).toBe("own");
    await getCachedSession("k", second);
    expect(second).toHaveBeenCalledTimes(2);
  });

  it("does not keep a signed-out answer", async () => {
    const load = loadOf({ isAuthenticated: false });

    await getCachedSession("k", load);
    await getCachedSession("k", load);

    expect(load).toHaveBeenCalledTimes(2);
  });

  it("keeps an answer no longer than the access token lives", async () => {
    let t = Date.parse("2026-01-01T00:00:00Z");
    const load = loadOf({ ...signedIn(), expiresAt: new Date(t + 5_000).toISOString() });

    await getCachedSession("k", load, { now: () => t });
    t += 6_000;
    await getCachedSession("k", load, { now: () => t });

    expect(load).toHaveBeenCalledTimes(2);
  });

  it("hands each caller its own copy", async () => {
    const load = loadOf({ ...signedIn(), preferences: { units: "mmol" } });

    const a = await getCachedSession("k", load);
    (a.preferences as { units: string }).units = "mg";
    const b = await getCachedSession("k", load);

    expect((b.preferences as { units: string }).units).toBe("mmol");
  });

  it("asks afresh for a request that may write, without keeping its answer", async () => {
    const load = loadOf(signedIn());

    await getCachedSession("k", load, { fresh: true });
    await getCachedSession("k", load);

    expect(load).toHaveBeenCalledTimes(2);
  });

  it("asks again once a write has forgotten the answer", async () => {
    const load = loadOf(signedIn());
    await getCachedSession("k", load);

    forgetSession("k");
    await getCachedSession("k", load);

    expect(load).toHaveBeenCalledTimes(2);
  });

  it("does not keep an answer whose lookup was running when a write finished", async () => {
    let answer!: (value: SessionLoad) => void;
    const slow = vi.fn(() => new Promise<SessionLoad>((resolve) => (answer = resolve)));
    const read = getCachedSession("k", slow);

    forgetSession("k");
    answer({ session: signedIn("before"), cacheable: true });
    await read;
    const next = loadOf(signedIn("after"));

    expect((await getCachedSession("k", next)).name).toBe("after");
  });

  it("keys different hosts and tokens apart", () => {
    expect(sessionCacheKey(["t", undefined, undefined, "a.example", "https"]))
      .not.toBe(sessionCacheKey(["t", undefined, undefined, "b.example", "https"]));
    expect(sessionCacheKey(["t1", "r"])).not.toBe(sessionCacheKey(["t1r", ""]));
  });
});
