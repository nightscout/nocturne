import { describe, expect, it, vi } from "vitest";
import { minutes } from "./format";

vi.mock("#lib/utils/formatting.js", () => ({ formatLocale: () => "en-US" }));

describe("minutes", () => {
  it("writes whole minutes with the locale's unit", () => {
    expect(minutes(75)).toBe("75 min");
    expect(minutes(4.6)).toBe("5 min");
  });
});
