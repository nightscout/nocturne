import { describe, it, expect } from "vitest";
import { markerKey } from "./keys";

describe("markerKey", () => {
  const at = new Date("2026-08-29T10:00:00Z");

  it("is the id when there is one", () => {
    expect(markerKey("a1", at, 3)).toBe("a1");
  });

  it.each<{ kind: string; id: string | null | undefined }>([
    { kind: "empty", id: "" },
    { kind: "missing", id: undefined },
    { kind: "null", id: null },
  ])("falls back to time and position for a $kind id", ({ id }) => {
    expect(markerKey(id, at, 3)).toBe(`${at.getTime()}-3`);
  });

  it("gives two id-less rows at one time distinct keys", () => {
    expect(markerKey("", at, 0)).not.toBe(markerKey("", at, 1));
  });
});
