import { describe, it, expect } from "vitest";
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import {
  STATUS_TOKENS,
  oklchToHex,
  statusHex,
  statusRgb,
  statusVar,
  type StatusToken,
} from "@nocturne/ui/tokens";

/**
 * The drift guard for `@nocturne/ui`'s status tokens. It lives in this package
 * because the bot is the consumer that needs the resolved bytes and because
 * `@nocturne/ui` ships no test runner of its own.
 */
const themeCss = () => {
  const themeEntry = createRequire(import.meta.url).resolve("@nocturne/ui/theme.css");
  return readFileSync(join(dirname(themeEntry), "styles", "nocturne-theme.css"), "utf8");
};

describe("status tokens", () => {
  it("states the same colour the default theme does", () => {
    const css = themeCss();
    for (const [token, value] of Object.entries(STATUS_TOKENS)) {
      expect(css).toContain(`--status-${token}: ${value};`);
    }
  });

  it("resolves each token to the sRGB bytes a chat platform takes", () => {
    expect(statusHex("critical")).toBe("#e7000b");
    expect(statusHex("warning")).toBe("#f54900");
    expect(statusHex("info")).toBe("#2389e2");
    expect(statusRgb("critical")).toBe(0xe7000b);
  });

  it("keeps the browser reference theme-following", () => {
    expect(statusVar("critical")).toBe("var(--status-critical)");
  });

  it("refuses a colour it cannot convert rather than emitting a wrong one", () => {
    expect(() => oklchToHex("rgb(235, 87, 87)")).toThrow();
    expect(() => oklchToHex("")).toThrow();
    // A number the pattern once accepted and Number() then turned into NaN,
    // which reached the output as "#NaNNaNNaN" instead of raising.
    expect(() => oklchToHex("oklch(1.2.3 0.245 27.325)")).toThrow();
    expect(() => oklchToHex("oklch(0.577 0.2.4 27.325)")).toThrow();
  });

  it("clips to the gamut instead of wrapping past it", () => {
    const token: StatusToken = "critical";
    expect(oklchToHex(STATUS_TOKENS[token])).toMatch(/^#[0-9a-f]{6}$/);
    expect(oklchToHex("oklch(0.9 0.4 27)")).toMatch(/^#[0-9a-f]{6}$/);
  });
});
