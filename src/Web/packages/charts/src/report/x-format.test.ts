import { describe, expect, it } from "vitest";
import { formatMinuteOfDay, formatOffset } from "./types.js";

const format = { locale: "en-GB", hour12: false };

describe("the band charts' horizontal axis words", () => {
  it("write a time of day and a signed offset", () => {
    expect(formatMinuteOfDay(450, format)).toBe("7:30");
    expect(formatOffset(-30, format)).toBe("-30");
    expect(formatOffset(90, format)).toBe("+90");
  });

  it("write nothing for a slot the pointer is not over, rather than throw", () => {
    expect(formatMinuteOfDay(Number.NaN, format)).toBe("");
    expect(formatMinuteOfDay(undefined as unknown as number, format)).toBe("");
    expect(formatOffset(Number.NaN, format)).toBe("");
  });
});
