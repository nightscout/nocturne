import { describe, it, expect } from "vitest";
import { distinct, groupBy, indexBy, toggled, uniqueBy, withAll } from "./collections";

describe("collections", () => {
  it("distinct keeps first-seen order and drops null and undefined", () => {
    expect(distinct(["b", "a", null, "b", "", undefined, "c"])).toEqual(["b", "a", "", "c"]);
  });

  it("uniqueBy keeps the first item per key and drops keyless items", () => {
    const items = [{ id: "1", n: 1 }, { id: undefined, n: 2 }, { id: "1", n: 3 }, { id: "2", n: 4 }];
    expect(uniqueBy(items, (i) => i.id).map((i) => i.n)).toEqual([1, 4]);
  });

  it("groupBy buckets in first-seen order", () => {
    const groups = groupBy(["apple", "avocado", "banana", ""], (s) => s[0]);
    expect([...groups]).toEqual([
      ["a", ["apple", "avocado"]],
      ["b", ["banana"]],
    ]);
  });

  it("indexBy maps each key to the last item's value", () => {
    const index = indexBy([{ k: "x", v: 1 }, { k: null, v: 2 }, { k: "x", v: 3 }], (i) => i.k, (i) => i.v);
    expect([...index]).toEqual([["x", 3]]);
  });

  it("toggled returns a copy with the value flipped or forced", () => {
    const set = new Set(["a"]);
    expect([...toggled(set, "a")]).toEqual([]);
    expect([...toggled(set, "b")]).toEqual(["a", "b"]);
    expect([...toggled(set, "a", true)]).toEqual(["a"]);
    expect([...toggled(set, "b", false)]).toEqual(["a"]);
    expect([...set]).toEqual(["a"]);
  });

  it("withAll returns a copy with every value added", () => {
    const set = new Set([1]);
    expect([...withAll(set, [2, 1, 3])]).toEqual([1, 2, 3]);
    expect([...set]).toEqual([1]);
  });
});
