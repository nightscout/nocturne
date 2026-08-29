import { describe, it, expect, vi } from "vitest";
import { stableBy } from "./stable-by";

describe("stableBy", () => {
  it("rebuilds only when a key changes", () => {
    const build = vi.fn((a: number, b: string) => ({ a, b }));
    const memo = stableBy(build);

    const first = memo(1, "x");
    expect(memo(1, "x")).toBe(first);
    expect(build).toHaveBeenCalledTimes(1);

    const second = memo(2, "x");
    expect(second).not.toBe(first);
    expect(build).toHaveBeenCalledTimes(2);

    // A key it has moved away from is not remembered — one entry, not a cache.
    expect(memo(1, "x")).not.toBe(first);
    expect(build).toHaveBeenCalledTimes(3);
  });

  it("cannot see a key mutated in place", () => {
    // The contract, pinned so it is not mistaken for a bug later: parts are
    // compared by identity, so an object key is only safe when whatever owns it
    // replaces it wholesale. `mergeGlucose` relies on exactly that of the
    // realtime store's `$state.raw` entries array.
    const build = vi.fn((rows: number[]) => rows.length);
    const memo = stableBy(build);
    const rows = [1];

    expect(memo(rows)).toBe(1);
    rows.push(2);
    expect(memo(rows)).toBe(1);
    expect(build).toHaveBeenCalledTimes(1);

    expect(memo([...rows])).toBe(2);
  });

  it("treats NaN bounds as unchanged", () => {
    // An unparseable dateRange yields NaN bounds; `===` would thrash on them and
    // rebuild forever, which is the failure this memo exists to avoid.
    const memo = stableBy((from: number, to: number) => ({ from, to }));

    const first = memo(NaN, NaN);
    expect(memo(NaN, NaN)).toBe(first);
  });

  it("distinguishes undefined from a missing argument", () => {
    const build = vi.fn((...key: unknown[]) => key.length);
    const memo = stableBy(build);

    expect(memo(undefined)).toBe(1);
    expect(memo()).toBe(0);
    expect(build).toHaveBeenCalledTimes(2);
  });

  it("does not serve a stale value after the build throws", () => {
    let fail = true;
    const memo = stableBy((n: number) => {
      if (fail) throw new Error("nope");
      return { n };
    });

    fail = false;
    expect(memo(1)).toEqual({ n: 1 });

    fail = true;
    expect(() => memo(2)).toThrow("nope");

    // Recording key 2 beside value 1 would hand that stale value to every later
    // caller asking for 2.
    fail = false;
    expect(memo(2)).toEqual({ n: 2 });
  });
});
