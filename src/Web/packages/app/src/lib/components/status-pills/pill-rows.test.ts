import { describe, expect, it } from "vitest";
import { formatPillRows, parsePillRows } from "./pill-rows";

describe("pill rows cookie", () => {
  it("reads both rails", () => {
    expect(parsePillRows("2.3")).toEqual({ desktop: 2, mobile: 3 });
  });

  it("caps a rail at four rows", () => {
    expect(parsePillRows("9.1")).toEqual({ desktop: 4, mobile: 1 });
  });

  it.each([undefined, null, "", "2", "a.b", "12.1"])("reads %s as one row each", (value) => {
    expect(parsePillRows(value)).toEqual({ desktop: 1, mobile: 1 });
  });

  it("writes what it reads", () => {
    expect(formatPillRows({ desktop: 2, mobile: 1 })).toBe("2.1");
  });
});
