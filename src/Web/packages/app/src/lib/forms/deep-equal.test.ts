import { describe, it, expect } from "vitest";
import { deepEqual } from "./deep-equal";

describe("deepEqual", () => {
  it("returns true for identical primitives", () => {
    expect(deepEqual("a", "a")).toBe(true);
    expect(deepEqual(1, 1)).toBe(true);
    expect(deepEqual(true, true)).toBe(true);
    expect(deepEqual(null, null)).toBe(true);
    expect(deepEqual(undefined, undefined)).toBe(true);
  });

  it("returns false for different primitives", () => {
    expect(deepEqual("a", "b")).toBe(false);
    expect(deepEqual(1, 2)).toBe(false);
    expect(deepEqual(null, undefined)).toBe(false);
  });

  it("compares plain objects deeply", () => {
    expect(deepEqual({ a: 1, b: "x" }, { a: 1, b: "x" })).toBe(true);
    expect(deepEqual({ a: 1 }, { a: 2 })).toBe(false);
    expect(deepEqual({ a: 1 }, { a: 1, b: 2 })).toBe(false);
  });

  it("compares nested objects", () => {
    expect(deepEqual({ a: { b: 1 } }, { a: { b: 1 } })).toBe(true);
    expect(deepEqual({ a: { b: 1 } }, { a: { b: 2 } })).toBe(false);
  });

  it("compares arrays", () => {
    expect(deepEqual([1, 2, 3], [1, 2, 3])).toBe(true);
    expect(deepEqual([1, 2], [1, 2, 3])).toBe(false);
    expect(deepEqual([{ a: 1 }], [{ a: 1 }])).toBe(true);
  });

  it("compares Date objects by value", () => {
    const d1 = new Date("2026-01-01");
    const d2 = new Date("2026-01-01");
    const d3 = new Date("2026-06-01");
    expect(deepEqual(d1, d2)).toBe(true);
    expect(deepEqual(d1, d3)).toBe(false);
  });

  it("treats undefined and missing keys as equal", () => {
    expect(deepEqual({ a: 1, b: undefined }, { a: 1 })).toBe(true);
  });

  it("handles empty objects and arrays", () => {
    expect(deepEqual({}, {})).toBe(true);
    expect(deepEqual([], [])).toBe(true);
    expect(deepEqual({}, [])).toBe(false);
  });

  it("treats empty string and undefined as different", () => {
    expect(deepEqual({ a: "" }, { a: undefined })).toBe(false);
  });
});
